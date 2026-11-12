using System.Data;
using System.Text;
using System.Text.Json;

namespace Mixer.Core;
public record SourceFolders(string Extreme, string Emk, string Ncn)
{
    public static SourceFolders FromExtreme(string root) => new(root, Path.Combine(root, "Songs", "MIDI", "EMK"), Path.Combine(root, "Songs", "MIDI", "NCN"));
    public string ExportRoot => Path.Combine(Extreme, "Songs", "MIDI", "EMK NCN Mixer Export");
}
public record OutputFile(string Source, string Target);
public record PlannedSong(string OldCode, string NewCode, string Kind, List<OutputFile> Files, List<string> Problems);
public record ExportPlan(string ExportRoot, List<PlannedSong> Songs)
{
    public int Ready => Songs.Count(s => s.Problems.Count == 0);
    public int Missing => Songs.Count - Ready;
}
public record ExportProgress(int Done, int Total, string Kind, string Code, int EmkDone, int NcnDone);
public record ReconstructionResult(string Report, int Copied, int Moved, int Failed, int Skipped, bool Cancelled);
public static class Reconstruction
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public static ExportPlan Plan(DataTable kept, SourceFolders folders, char start, int perFolder, CancellationToken token = default)
    {
        int first = Alphabet.IndexOf(start);
        if (first < 0 || perFolder < 1 || perFolder > 99999) throw new ArgumentException("จำนวนเพลงต่อโฟลเดอร์ต้องเป็น 1–99,999 และตัวอักษรต้องเป็น 0–9 หรือ A–Z");
        string exportRoot = Path.GetFullPath(folders.ExportRoot);
        foreach (var source in new[] { folders.Emk, folders.Ncn })
            if (Inside(exportRoot, source) || Inside(source, exportRoot)) throw new InvalidOperationException("โฟลเดอร์ Export ต้องแยกจากโฟลเดอร์เพลงต้นทาง");
        var plan = new List<PlannedSong>();
        foreach (string kind in new[] { "EMK", "NCN" })
        {
            // Exact subtype matching at export is also the reference behavior.
            var table = new DataView(kept) { RowFilter = $"SUB_TYPE like '{kind}'" }.ToTable();
            if (table.Rows.Count > (Alphabet.Length - first) * perFolder) throw new InvalidOperationException($"เพลง {kind} เกินจำนวนที่ส่งออกได้จากตัวอักษร {start} ({(Alphabet.Length - first) * perFolder:N0} เพลง)");
            if (table.Rows.Count == 0) continue;
            var primaryRoot = kind == "EMK" ? folders.Emk : Path.Combine(folders.Ncn, "Song");
            var primary = Index(primaryRoot, token);
            var lyricCache = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            var cursorCache = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < table.Rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var old = table.Rows[i]["CODE"].ToString()!.Trim();
                var folder = Alphabet[first + i / perFolder].ToString();
                var code = folder + (i % perFolder + 1).ToString("00000");
                var files = new List<OutputFile>(); var errors = new List<string>();
                if (old.Length == 0 || old.IndexOfAny(['/', '\\', '\0']) >= 0) errors.Add("รหัสเพลงไม่ถูกต้อง");
                string? source = errors.Count == 0 ? Find(primary, old + (kind == "EMK" ? ".emk" : ".mid")) : null;
                if (source == null) errors.Add($"ไม่พบไฟล์หลัก {old}");
                else if (kind == "EMK") files.Add(new(source, Path.Combine(exportRoot, "EMK", "Export", folder, code + ".emk")));
                else
                {
                    files.Add(new(source, Path.Combine(exportRoot, "NCN", "Export", "Song", folder, code + ".mid")));
                    // The reference uses the last MIDI folder name, not FILE_NAME from DBF.
                    var last = Path.GetFileName(Path.GetDirectoryName(source));
                    foreach (var (branch, ext, cache) in new[] { ("Lyrics", ".Lyr", lyricCache), ("Cursor", ".Cur", cursorCache) })
                    {
                        string branchRoot = Path.Combine(folders.Ncn, branch, last!);
                        if (!cache.TryGetValue(branchRoot, out var index)) cache[branchRoot] = index = Index(branchRoot, token);
                        var companion = Find(index, old + ext);
                        if (companion == null) errors.Add($"ไม่พบ {branch}/{last}/{old}{ext}");
                        else files.Add(new(companion, Path.Combine(exportRoot, "NCN", "Export", branch, folder, code + ext)));
                    }
                }
                plan.Add(new(old, code, kind, files, errors));
            }
        }
        return new(exportRoot, plan);
    }
    static Dictionary<string, List<string>> Index(string root, CancellationToken token)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return index;
        // Root files are checked before recursive matches, just like findFilePath in the reference.
        foreach (var path in Directory.EnumerateFiles(root).Concat(Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(p => !string.Equals(Path.GetDirectoryName(p), Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!index.TryGetValue(name, out var list)) index[name] = list = [];
            list.Add(Path.GetFullPath(path));
        }
        return index;
    }
    static string? Find(Dictionary<string, List<string>> index, string name) => index.GetValueOrDefault(name)?.FirstOrDefault();
    public static bool Inside(string path, string root)
    {
        path = Path.GetFullPath(path); root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<ReconstructionResult> ExecuteAsync(ExportPlan plan, bool move, IProgress<ExportProgress>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(plan.ExportRoot);
        var report = Path.Combine(plan.ExportRoot, "report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6] + ".jsonl");
        int copied = 0, moved = 0, failed = 0, skipped = 0, emk = 0, ncn = 0, done = 0; bool cancelled = false;
        await using var log = new StreamWriter(report, false, new UTF8Encoding(false)) { AutoFlush = true };
        async Task Log(object value) => await log.WriteLineAsync(JsonSerializer.Serialize(value));
        await Log(new { action = "start", mode = move ? "move" : "copy", songs = plan.Songs.Count, date = DateTimeOffset.Now });
        try
        {
            foreach (var song in plan.Songs)
            {
                token.ThrowIfCancellationRequested();
                if (song.Problems.Count > 0) { skipped++; await Log(new { action = "skip-song", song.OldCode, song.NewCode, song.Kind, problems = song.Problems }); }
                else
                {
                    try
                    {
                        var files = song.Files;
                        // A collision of the main file prefixes all NCN companions with the same minute stamp.
                        string prefix = File.Exists(files[0].Target) ? DateTime.Now.ToString("yyyyMMddHHmm") + "_" : "";
                        var targets = files.Select(f => prefix.Length == 0 ? f.Target : Path.Combine(Path.GetDirectoryName(f.Target)!, prefix + Path.GetFileName(f.Target))).ToArray();
                        for (int i = 0; i < files.Count; i++)
                        {
                            if (!Inside(targets[i], plan.ExportRoot)) throw new IOException("เส้นทาง Export ไม่ถูกต้อง");
                            if (File.Exists(targets[i])) throw new IOException($"มีไฟล์ปลายทางอยู่แล้ว: {targets[i]}");
                            if (!File.Exists(files[i].Source) || HasLink(files[i].Source)) throw new IOException($"ไฟล์ต้นทางหายหรือเป็นลิงก์: {files[i].Source}");
                        }
                        for (int i = 0; i < files.Count; i++)
                        {
                            // Cancellation is between songs so NCN sets are not split by a user cancellation.
                            var file = files[i]; var target = targets[i];
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            await Log(new { action = "planned", song.OldCode, song.NewCode, source = file.Source, target, move });
                            if (move) { File.Move(file.Source, target, false); moved++; }
                            else
                            {
                                var temporary = target + ".partial-" + Guid.NewGuid().ToString("N");
                                try
                                {
                                    await using (var source = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                                        await source.CopyToAsync(output, CancellationToken.None);
                                    File.Move(temporary, target, false); copied++;
                                }
                                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                            }
                            await Log(new { action = move ? "moved" : "copied", source = file.Source, target });
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; await Log(new { action = "failed-song", song.OldCode, song.NewCode, error = ex.Message }); }
                }
                if (song.Kind == "EMK") emk++; else ncn++;
                progress?.Report(new(++done, plan.Songs.Count, song.Kind, song.OldCode, emk, ncn));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        await Log(new { action = "end", copied, moved, failed, skipped, cancelled });
        return new(report, copied, moved, failed, skipped, cancelled);
    }
    static bool HasLink(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
