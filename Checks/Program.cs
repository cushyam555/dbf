using Mixer.Core;
var temp = Path.Combine(Path.GetTempPath(), "mixer-check-" + Guid.NewGuid());
Directory.CreateDirectory(temp);
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); }
try
{
    var root = Path.Combine(temp, "input"); var dest = Path.Combine(temp, "output");
    foreach (var dir in new[] { "Midi/A", "Lyrics/A", "Cursor/A", "EMK" }) Directory.CreateDirectory(Path.Combine(root, dir));
    File.WriteAllText(Path.Combine(root, "Midi/A/001.mid"), "midi-test");
    File.WriteAllText(Path.Combine(root, "Lyrics/A/001.lyr"), "เนื้อเพลง");
    File.WriteAllText(Path.Combine(root, "Cursor/A/001.cur"), "cursor");
    File.WriteAllText(Path.Combine(root, "Midi/A/002.mid"), "missing");
    File.WriteAllText(Path.Combine(root, "EMK/003.emk"), "emk-test");
    File.WriteAllText(Path.Combine(root, "Lyrics/A/orphan.lyr"), "orphan");
    var scan = Library.Scan(root + Path.DirectorySeparatorChar);
    Check(scan.Songs.Count == 3, "scan primary files");
    Check(scan.Songs.Single(x => x.Code == "001").Files.Length == 3, "NCN companions across folders");
    Check(scan.Songs.Single(x => x.Code == "002").Status.Contains("ขาด"), "missing companion detection");
    Check(scan.Warnings.Count == 1, "orphan detection");
    var result = await Library.ExportAsync(scan, scan.Songs, dest, null, default);
    Check(result.Copied == 5 && result.Failed == 0, "export deduplicated file set");
    foreach (var song in scan.Songs) foreach (var source in song.Files)
        Check(File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(Path.Combine(result.Folder, Path.GetRelativePath(root, source)))), "copied bytes " + Path.GetFileName(source));
    Check(File.Exists(Path.Combine(result.Folder, "export-report.json")), "export report");
    var twice = await Library.ExportAsync(scan, scan.Songs, dest, null, default);
    Check(twice.Folder != result.Folder && File.Exists(Path.Combine(result.Folder, "Midi/A/001.mid")), "separate exports preserve previous output");
    bool rejected = false;
    try { await Library.ExportAsync(scan, scan.Songs, Path.Combine(root, "nested"), null, default); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "reject destination inside source");
    rejected = false;
    try { await Library.ExportAsync(scan, scan.Songs, temp, null, default); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "reject destination containing source");
    Directory.CreateDirectory(Path.Combine(root, "duplicate"));
    File.WriteAllText(Path.Combine(root, "duplicate/001.lyr"), "duplicate");
    var ambiguous = Library.Scan(root).Songs.Single(x => x.Code == "001");
    Check(ambiguous.Status.Contains("ซ้ำ") && ambiguous.Files.Length == 2, "ambiguous companion not silently chosen");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var cancelled = await Library.ExportAsync(scan, scan.Songs, dest, null, cancellation.Token);
    Check(cancelled.Cancelled && cancelled.Copied == 0 && File.Exists(Path.Combine(cancelled.Folder, "export-report.json")), "cancellation retains report");
    using var during = new CancellationTokenSource();
    var stopped = await Library.ExportAsync(scan, scan.Songs, dest, new InlineProgress(_ => during.Cancel()), during.Token);
    Check(stopped.Cancelled && stopped.Copied == 1, "stop between files retains completed copy");
    Check(!Directory.EnumerateFiles(stopped.Folder, "*.partial-*", SearchOption.AllDirectories).Any(), "no partial files after cancellation");
    File.Delete(Path.Combine(root, "EMK/003.emk"));
    var missing = await Library.ExportAsync(scan, scan.Songs.Where(s => s.Code == "003"), dest, null, default);
    Check(missing.Failed == 1 && missing.Copied == 0, "source removed after scan reported");
    Directory.CreateDirectory(Path.Combine(root, "Other"));
    File.WriteAllText(Path.Combine(root, "Other/001.mid"), "duplicate midi");
    Check(Library.Scan(root).Songs.Where(s => s.Code == "001").All(s => s.Status.Contains("ซ้ำ")), "duplicate MIDI codes require manual review");
    Console.WriteLine("All checks passed.");
}
finally { Directory.Delete(temp, true); }

class InlineProgress(Action<(int Done, int Total, string File)> action) : IProgress<(int Done, int Total, string File)> { public void Report((int Done, int Total, string File) value) => action(value); }
