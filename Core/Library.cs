using System.Text;
using System.Text.Json;

namespace Mixer.Core;

public record Song(string Code, string Kind, string Status, string RelativePath, string[] Files, long Bytes);
public record ScanResult(string Root, List<Song> Songs, List<string> Warnings);
public record ExportResult(string Folder, int Copied, int Skipped, int Failed, bool Cancelled);
public static class Library
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".emk", ".mid", ".midi", ".lyr", ".cur" };
    public static bool IsInside(string path, string root)
    {
        path = Path.GetFullPath(path); root = Path.GetFullPath(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    public static ScanResult Scan(string root, CancellationToken token = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("ไม่พบโฟลเดอร์ต้นทาง");
        var files = new List<string>(); var warnings = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    token.ThrowIfCancellationRequested();
                    if (Extensions.Contains(Path.GetExtension(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) files.Add(path);
                }
                foreach (var child in Directory.EnumerateDirectories(dir))
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { warnings.Add($"{dir}: {e.Message}"); }
        }
        var companions = files.Where(f => Path.GetExtension(f).Equals(".lyr", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".cur", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key!, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var midiCounts = files.Where(f => Path.GetExtension(f).Equals(".mid", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".midi", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key!, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Song>();
        foreach (var file in files.Where(f => new[] { ".emk", ".mid", ".midi" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            token.ThrowIfCancellationRequested();
            var emk = Path.GetExtension(file).Equals(".emk", StringComparison.OrdinalIgnoreCase);
            var code = Path.GetFileNameWithoutExtension(file); var group = new List<string> { file };
            var issues = new List<string>();
            if (!emk)
            {
                foreach (var ext in new[] { ".lyr", ".cur" })
                {
                    var candidates = companions.GetValueOrDefault(code, []).Where(p => Path.GetExtension(p).Equals(ext, StringComparison.OrdinalIgnoreCase)).ToArray();
                    var sameDir = candidates.Where(p => Path.GetDirectoryName(p)!.Equals(Path.GetDirectoryName(file), StringComparison.OrdinalIgnoreCase)).ToArray();
                    var match = sameDir.Length == 1 ? sameDir : candidates;
                    if (sameDir.Length == 0 && midiCounts.GetValueOrDefault(code) > 1 && match.Length > 0)
                        issues.Add($"รหัส MIDI ซ้ำ: ต้องตรวจสอบ {ext}");
                    else if (match.Length == 1) { group.Add(match[0]); used.Add(match[0]); }
                    else issues.Add(match.Length == 0 ? $"ขาด {ext}" : $"{ext} ซ้ำ: ต้องตรวจสอบ");
                }
            }
            long bytes = 0;
            foreach (var p in group) { try { bytes += new FileInfo(p).Length; } catch (IOException e) { issues.Add("อ่านขนาดไม่ได้"); warnings.Add(e.Message); } }
            rows.Add(new(code, emk ? "EMK" : "NCN / MIDI", issues.Count == 0 ? (emk ? "พร้อมคัดลอก" : "พบไฟล์ประกอบครบ") : string.Join(" • ", issues), Path.GetRelativePath(root, file), group.ToArray(), bytes));
        }
        foreach (var orphan in files.Where(f => (Path.GetExtension(f).Equals(".lyr", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(f).Equals(".cur", StringComparison.OrdinalIgnoreCase)) && !used.Contains(f)))
            warnings.Add($"ไฟล์ประกอบที่ยังจับคู่ไม่ได้: {Path.GetRelativePath(root, orphan)}");
        return new(root, rows.OrderBy(s => s.Code, StringComparer.OrdinalIgnoreCase).ToList(), warnings);
    }
    public static async Task<ExportResult> ExportAsync(ScanResult scan, IEnumerable<Song> selected, string destination, IProgress<(int Done, int Total, string File)>? progress, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (IsInside(destination, scan.Root) || IsInside(scan.Root, destination)) throw new InvalidOperationException("เลือกปลายทางที่แยกจากต้นทาง และไม่เป็นโฟลเดอร์แม่ของต้นทาง");
        var files = selected.SelectMany(s => s.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("ยังไม่ได้เลือกไฟล์");
        Directory.CreateDirectory(destination);
        var run = Path.Combine(destination, "MixerExport_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(run);
        var log = new List<object>(); int copied = 0, skipped = 0, failed = 0; bool cancelled = false;
        try
        {
            foreach (var source in files)
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(scan.Root, source);
                if (!IsInside(source, scan.Root)) { failed++; log.Add(new { file = source, status = "outside-source" }); continue; }
                var target = Path.GetFullPath(Path.Combine(run, relative));
                if (!IsInside(target, run)) throw new IOException("เส้นทางปลายทางไม่ถูกต้อง");
                string? temp = null;
                try
                {
                    // Do not follow a source file redirected after the scan.
                    var info = new FileInfo(source);
                    if (!info.Exists || HasLinkComponent(source, scan.Root)) throw new IOException("ไฟล์หายหรือเป็นลิงก์ กรุณาสแกนใหม่");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) { skipped++; log.Add(new { file = relative, status = "skipped-existing" }); continue; }
                    temp = target + ".partial-" + Guid.NewGuid().ToString("N");
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await input.CopyToAsync(output, token);
                    File.Move(temp, target, false); temp = null;
                    copied++; log.Add(new { file = relative, status = "copied" });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; log.Add(new { file = relative, status = "failed", error = e.Message }); }
                finally { if (temp != null && File.Exists(temp)) File.Delete(temp); }
                progress?.Report((copied + skipped + failed, files.Length, relative));
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        await File.WriteAllTextAsync(Path.Combine(run, "export-report.json"), JsonSerializer.Serialize(new { copied, skipped, failed, cancelled, files = log }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, CancellationToken.None);
        return new(run, copied, skipped, failed, cancelled);
    }
    static bool HasLinkComponent(string file, string root)
    {
        var current = file;
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            current = Path.GetDirectoryName(current) ?? throw new IOException("เส้นทางไม่ถูกต้อง");
        }
    }
}
