using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Mixer.Core;
public record HoldEntry(string Source, string Relative, string Code, string Kind);
public record HoldManifest(int Version, DateTimeOffset Created, SourceFolders Folders, List<HoldEntry> Entries);
public record HoldEvent(int Index, string State, string? Hash = null, string? Error = null);
public record HoldResult(string Manifest, int Moved, int Failed, int Skipped, bool Cancelled);
public record RestoreResult(int Restored, int AlreadyRestored, int Failed, bool Cancelled, string[] Errors);
public static class Hold
{
    public const string ManifestName = "mixer-hold.json";
    const string JournalName = "mixer-hold.journal.jsonl";
    public static ExportPlan Plan(DataTable duplicates, DataTable kept, SourceFolders folders, CancellationToken token = default)
    {
        var source = Reconstruction.Plan(duplicates, folders, '0', null, token);
        var protectedFiles = Reconstruction.Plan(kept, folders, '0', null, token).Songs.SelectMany(s => s.Files).Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(folders.ExportRoot, "พักเพลงซ้ำ", DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        var songs = new List<PlannedSong>(); var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in source.Songs)
        {
            var errors = song.Problems.ToList();
            if (song.Files.Any(f => protectedFiles.Contains(f.Source))) errors.Add("ใช้ไฟล์ร่วมกับเพลงที่เก็บไว้ จึงไม่พักชุดนี้");
            if (song.Files.Any(f => assigned.Contains(f.Source))) errors.Add("ไฟล์เดียวกับรายการซ้ำที่วางแผนไว้แล้ว");
            var files = new List<OutputFile>();
            foreach (var f in song.Files)
            {
                var sourceRoot = song.Kind == "EMK" ? folders.Emk : folders.Ncn;
                if (!Reconstruction.Inside(f.Source, sourceRoot)) { errors.Add("ไฟล์อยู่นอกต้นทาง"); continue; }
                files.Add(new(f.Source, Path.Combine(root, song.Kind, Path.GetRelativePath(sourceRoot, f.Source))));
            }
            if (errors.Count == 0) foreach (var f in files) assigned.Add(f.Source);
            songs.Add(new(song.OldCode, song.OldCode, song.Kind, files, errors));
        }
        return new(root, songs);
    }
    static void NoLinks(string path)
    {
        for (string? p = Path.GetFullPath(path); p != null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("ไม่รองรับเส้นทางลิงก์: " + p);
    }
    static async Task<string> Hash(string path) { await using var f = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(f)); }
    static async Task Append(StreamWriter writer, HoldEvent item) { await writer.WriteLineAsync(JsonSerializer.Serialize(item)); await writer.FlushAsync(); if (writer.BaseStream is FileStream stream) stream.Flush(true); }
    static StreamWriter Journal(string root)
    {
        var path = Path.Combine(root, JournalName); NoLinks(path);
        if (File.Exists(path))
        {
            using var check = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            if (check.Length > 0)
            {
                check.Position = check.Length - 1;
                if (check.ReadByte() != '\n') { check.Position = check.Length; check.WriteByte((byte)'\n'); check.Flush(true); }
            }
        }
        return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }
    public static async Task<HoldResult> ExecuteAsync(ExportPlan plan, SourceFolders folders, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        if (Directory.Exists(plan.ExportRoot)) throw new IOException("โฟลเดอร์พักมีอยู่แล้ว กรุณาวางแผนใหม่");
        NoLinks(plan.ExportRoot);
        var entries = plan.Songs.Where(s => s.Problems.Count == 0).SelectMany(s => s.Files.Select(f => new HoldEntry(f.Source, Path.GetRelativePath(plan.ExportRoot, f.Target), s.OldCode, s.Kind))).ToList();
        foreach (var e in entries) Validate(e, plan.ExportRoot, folders);
        Directory.CreateDirectory(plan.ExportRoot);
        var manifest = Path.Combine(plan.ExportRoot, ManifestName);
        NoLinks(plan.ExportRoot);
        await using (var file = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(file, new HoldManifest(1, DateTimeOffset.Now, folders, entries), new JsonSerializerOptions { WriteIndented = true });
            await file.FlushAsync(); file.Flush(true);
        }
        await using var journal = Journal(plan.ExportRoot);
        int moved = 0, failed = 0, skipped = 0, done = 0, emk = 0, ncn = 0, index = 0; bool cancelled = false;
        try
        {
            foreach (var song in plan.Songs)
            {
                token.ThrowIfCancellationRequested();
                if (song.Problems.Count > 0) skipped++;
                else
                {
                    var completed = new List<(int Index, HoldEntry Entry, string Hash)>();
                    int firstIndex = index;
                    try
                    {
                        foreach (var file in song.Files)
                        {
                            var entry = entries[index]; Validate(entry, plan.ExportRoot, folders); NoLinks(entry.Source); NoLinks(file.Target);
                            if (!File.Exists(entry.Source) || File.Exists(file.Target)) throw new IOException("ไฟล์ต้นทางหายหรือมีปลายทางอยู่แล้ว: " + entry.Source);
                            string hash = await Hash(entry.Source);
                            await Append(journal, new(index, "intent", hash));
                            Directory.CreateDirectory(Path.GetDirectoryName(file.Target)!); File.Move(entry.Source, file.Target, false);
                            completed.Add((index, entry, hash));
                            await Append(journal, new(index, "parked", hash)); index++;
                        }
                        moved += completed.Count;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        failed++;
                        foreach (var item in completed.AsEnumerable().Reverse())
                        {
                            try
                            {
                                var parked = Path.Combine(plan.ExportRoot, item.Entry.Relative);
                                if (File.Exists(item.Entry.Source)) throw new IOException("ต้นทางมีไฟล์อยู่แล้ว จึงยังเก็บไฟล์นี้ไว้ในที่พัก");
                                await Append(journal, new(item.Index, "restore-intent", item.Hash)); File.Move(parked, item.Entry.Source, false);
                                await Append(journal, new(item.Index, "restored", item.Hash));
                            }
                            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { moved++; await Append(journal, new(item.Index, "parked", item.Hash, rollback.Message)); }
                        }
                        await Append(journal, new(firstIndex, "song-error", null, ex.Message));
                        index = firstIndex + song.Files.Count;
                    }
                }
                if (song.Kind == "EMK") emk++; else ncn++;
                progress?.Report(new(++done, plan.Songs.Count, song.Kind, song.OldCode, emk, ncn));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new(manifest, moved, failed, skipped, cancelled);
    }
    static void Validate(HoldEntry entry, string root, SourceFolders folders)
    {
        string allowed = entry.Kind == "EMK" ? folders.Emk : entry.Kind == "NCN" ? folders.Ncn : throw new InvalidDataException("ชนิดเพลงในรายการพักไม่ถูกต้อง");
        if (string.IsNullOrWhiteSpace(allowed) || string.IsNullOrWhiteSpace(entry.Source) || !Path.IsPathFullyQualified(entry.Source) || !Reconstruction.Inside(entry.Source, allowed) || string.IsNullOrWhiteSpace(entry.Relative) || Path.IsPathRooted(entry.Relative) || !Reconstruction.Inside(Path.Combine(root, entry.Relative), root)) throw new InvalidDataException("เส้นทางในรายการพักไม่ตรงกับโฟลเดอร์เพลงที่เลือก");
    }
    public static async Task<RestoreResult> RestoreAsync(string manifestPath, SourceFolders folders, CancellationToken token)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!; NoLinks(manifestPath);
        var manifest = JsonSerializer.Deserialize<HoldManifest>(await File.ReadAllTextAsync(manifestPath)) ?? throw new InvalidDataException("รายการกู้คืนไม่ถูกต้อง");
        if (manifest.Version != 1 || manifest.Entries == null) throw new InvalidDataException("รุ่นรายการกู้คืนไม่รองรับ");
        foreach (var entry in manifest.Entries) Validate(entry, root, folders);
        var states = new Dictionary<int, HoldEvent>();
        var journalPath = Path.Combine(root, JournalName); NoLinks(journalPath);
        if (!File.Exists(journalPath)) throw new InvalidDataException("ไม่พบประวัติการพักไฟล์");
        bool trimTail = false;
        using (var reader = new StreamReader(journalPath))
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                try
                {
                    var e = JsonSerializer.Deserialize<HoldEvent>(line) ?? throw new JsonException();
                    if (e.Index < 0 || e.Index >= manifest.Entries.Count) throw new InvalidDataException("ลำดับไฟล์ในประวัติไม่ถูกต้อง");
                    if (e.Hash == null && states.TryGetValue(e.Index, out var previous)) e = e with { Hash = previous.Hash };
                    states[e.Index] = e;
                }
                catch (JsonException) { if (!reader.EndOfStream) throw new InvalidDataException("ประวัติการพักเสียหาย"); trimTail = true; }
            }
        }
        if (trimTail)
        {
            using var repair = new FileStream(journalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            long end = repair.Length;
            while (end > 0) { repair.Position = --end; if (repair.ReadByte() == '\n') { end++; break; } }
            repair.SetLength(end); repair.Flush(true);
        }
        int restored = 0, already = 0, failed = 0; bool cancelled = false; var errors = new List<string>();
        await using var journal = Journal(root);
        try
        {
            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!states.TryGetValue(i, out var state) || state.Hash == null) continue;
                var entry = manifest.Entries[i]; var parked = Path.GetFullPath(Path.Combine(root, entry.Relative));
                try
                {
                    NoLinks(parked); NoLinks(entry.Source);
                    if (!File.Exists(parked) && File.Exists(entry.Source) && await Hash(entry.Source) == state.Hash) { already++; continue; }
                    if (File.Exists(entry.Source)) throw new IOException("ต้นทางมีไฟล์อยู่แล้ว ไม่เขียนทับ: " + entry.Source);
                    if (!File.Exists(parked)) throw new IOException("ไม่พบไฟล์ที่พัก: " + parked);
                    if (await Hash(parked) != state.Hash) throw new IOException("ไฟล์ที่พักเปลี่ยนแปลง: " + parked);
                    Directory.CreateDirectory(Path.GetDirectoryName(entry.Source)!);
                    await Append(journal, new(i, "restore-intent", state.Hash)); File.Move(parked, entry.Source, false);
                    await Append(journal, new(i, "restored", state.Hash)); restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; errors.Add(ex.Message); await Append(journal, new(i, "restore-failed", state.Hash, ex.Message)); }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        return new(restored, already, failed, cancelled, errors.ToArray());
    }
}
