using System.Data;
using System.Text;
using System.Text.Json;
using Mixer.Core;

var temporary = Path.Combine(Path.GetTempPath(), "mixer-compat-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
void Throws<T>(Action test, string name) where T : Exception { try { test(); } catch (T) { Check(true, name); return; } throw new Exception("FAIL (no exception): " + name); }
string Codes(DataTable table) => string.Join("|", table.Rows.Cast<DataRow>().Select(r => r["CODE"].ToString()!.Trim()));
try
{
    // Compare against the exact grouping and DataView sort expressions observed in the supplied reference.
    var metadata = Fixture.Table();
    Fixture.Add(metadata, "N01", "NCN", "เพลง ก", "นักร้อง ก", "เนื้อ ก");
    Fixture.Add(metadata, "E01", "EMK", "เพลง ก", "นักร้อง ก", "เนื้อ ก");
    Fixture.Add(metadata, "E02", "EMK", "เพลง ก", "นักร้อง ข", "เนื้อ ก");
    Fixture.Add(metadata, "N02", "NCN", "เพลง ข", "นักร้อง ก", "เนื้อ ก");
    Fixture.Add(metadata, "E03", "EMK", "เพลง ก", "นักร้อง ก", "เนื้อ ข");
    Fixture.Add(metadata, "E04", "EMK", "เพลง ก", "นักร้อง ก", "เนื้อ ก");
    Fixture.Add(metadata, "LOW", "NCN", "Song", "Artist", "Line");
    Fixture.Add(metadata, "UP", "EMK", "song", "Artist", "Line");
    Fixture.Add(metadata, "PAD", "NCN", "Song ", "Artist", "Line ");
    Fixture.Add(metadata, "EMPTY1", "EMK", "", "", "");
    Fixture.Add(metadata, "EMPTY2", "NCN", "", "", "");
    Fixture.Add(metadata, "NONMIDI", "NCN", "อื่น", "อื่น", "อื่น", "AUDIO");
    Fixture.Add(metadata, "EXCLUDED", "EMK", "อื่น", "อื่น", "อื่น", "AUDIO");
    Fixture.Add(metadata, "EXCLUDED2", "OTHER", "อื่น", "อื่น", "อื่น");
    Fixture.Add(metadata, "SUBSTRING", "EMK-X", "ใหม่", "ใหม่", "ใหม่");
    foreach (bool emkFirst in new[] { true, false })
    foreach (var mode in Enum.GetValues<DuplicateRule>())
    {
        var eligible = SongSelection.Eligible(metadata, emkFirst);
        var actual = SongSelection.Filter(eligible, mode, emkFirst);
        var expected = ReferenceOracle.Select(metadata, mode, emkFirst);
        Check(Codes(actual.Kept) == Codes(expected), $"reference differential: {mode}, EMK-first={emkFirst}");
        Check(actual.Kept.Rows.Count + actual.Duplicates.Rows.Count == eligible.Rows.Count, "kept + duplicate partition");
        Check(actual.Review.Rows.Count == eligible.Rows.Count && actual.Review.Select("[ผลการคัด] = 'ซ้ำ'").Length == actual.Duplicates.Rows.Count, "review marks all duplicate decisions");
    }
    var chosen = SongSelection.Filter(SongSelection.Eligible(metadata, true), DuplicateRule.TitleAndLyric, true);
    Check(Codes(chosen.Kept).Contains("E01") && !Codes(chosen.Kept).Contains("N01") && !Codes(chosen.Kept).Contains("E04"), "EMK preferred and stable first same-type winner");
    Check(Codes(chosen.Kept).Contains("LOW") && Codes(chosen.Kept).Contains("UP") && Codes(chosen.Kept).Contains("PAD"), "no case or whitespace normalization of duplicate keys");
    Check(SongSelection.Eligible(metadata, true).Select("CODE = 'NONMIDI'").Length == 1, "reference filter OR precedence for NCN");
    Check(SongSelection.Eligible(metadata, true).Select("CODE = 'EXCLUDED'").Length == 0, "non-MIDI EMK excluded");
    Throws<InvalidDataException>(() => SongSelection.Eligible(new DataTable(), true), "missing database columns rejected");
    var empty = SongSelection.Filter(metadata.Clone(), DuplicateRule.TitleAndLyric, true);
    Check(empty.Kept.Rows.Count == 0, "empty database handled without CopyToDataTable crash");

    // Randomized differential cases use both reference rules and all priority combinations.
    var random = new Random(63821);
    for (int iteration = 0; iteration < 30; iteration++)
    {
        var input = Fixture.Table();
        for (int row = 0; row < 80; row++)
            Fixture.Add(input, "R" + row, random.Next(2) == 0 ? "EMK" : "NCN", "T" + random.Next(5), "A" + random.Next(5), "L" + random.Next(4));
        foreach (bool priority in new[] { true, false })
        foreach (var mode in Enum.GetValues<DuplicateRule>())
        {
            var result = SongSelection.Filter(SongSelection.Eligible(input, priority), mode, priority);
            Check(Codes(result.Kept) == Codes(ReferenceOracle.Select(input, mode, priority)), $"random differential {iteration}/{mode}/{priority}");
        }
    }

    var dbf = Path.Combine(temporary, "song.dbf");
    Fixture.WriteDbf(dbf, metadata, deletedRow: 3);
    var raw = DbfReader.Read(dbf);
    if (args.Length == 2 && args[0] == "--reference")
    {
        var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(args[1]));
        var parser = assembly.GetType("ParseDBF", true)!;
        var referenceTable = (DataTable)parser.GetMethod("ReadDBF")!.Invoke(null, [dbf])!;
        Check(referenceTable.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType)).SequenceEqual(raw.Columns.Cast<DataColumn>().Select(c => (c.ColumnName, c.DataType))), "actual supplied EXE parser: database schema equality");
        Check(referenceTable.Rows.Count == raw.Rows.Count, "actual supplied EXE parser: row count equality");
        for (int i = 0; i < raw.Rows.Count; i++) Check(raw.Rows[i].ItemArray.SequenceEqual(referenceTable.Rows[i].ItemArray), "actual supplied EXE parser: all field values record " + i);
        foreach (bool priority in new[] { true, false })
        foreach (var mode in Enum.GetValues<DuplicateRule>())
            Check(Codes(SongSelection.Filter(SongSelection.Eligible(raw, priority), mode, priority).Kept) == Codes(ReferenceOracle.Select(referenceTable, mode, priority)), "actual EXE DBF output through reference grouping: " + mode + "/" + priority);
    }
    Check(raw.Rows.Count == metadata.Rows.Count - 1, "DBF deleted records skipped");
    Check(raw.Rows[0]["TITLE"].ToString()!.TrimEnd() == "เพลง ก", "Windows-874 Thai decoding");
    Check(raw.Rows[0]["TITLE"].ToString()!.Length == 40, "DBF character padding preserved for comparisons");
    Check((int)raw.Rows[0]["TEMPO"] == 120 && (decimal)raw.Rows[0]["RATING"] == 1.25m, "DBF integer and decimal types");
    Check((DateTime)raw.Rows[0]["ADDED"] == new DateTime(2024, 8, 9) && (bool)raw.Rows[0]["ACTIVE"], "DBF date and logical fields");
    foreach (bool priority in new[] { true, false })
    foreach (var mode in Enum.GetValues<DuplicateRule>())
        Check(Codes(SongSelection.Filter(SongSelection.Load(dbf, priority), mode, priority).Kept) == Codes(ReferenceOracle.Select(raw, mode, priority)), "DBF-to-selection reference differential");
    var truncated = Path.Combine(temporary, "truncated.dbf"); File.WriteAllBytes(truncated, File.ReadAllBytes(dbf)[..100]);
    Throws<InvalidDataException>(() => DbfReader.Read(truncated), "truncated DBF rejected");
    var zero = Path.Combine(temporary, "empty.dbf"); Fixture.WriteDbf(zero, metadata.Clone());
    Check(DbfReader.Read(zero).Rows.Count == 0, "empty DBF accepted");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel(); Throws<OperationCanceledException>(() => DbfReader.Read(dbf, cancelled.Token), "database read cancellation");
    }

    // A real-layout fixture: EMK A and NCN B are duplicates; NCN C is distinct.
    var extreme = Path.Combine(temporary, "Extreme"); var folders = SourceFolders.FromExtreme(extreme);
    void Put(string relative, string body) { var p = Path.Combine(extreme, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, body); }
    Put("Songs/MIDI/EMK/old/A.emk", "emk-A-original");
    foreach (string code in new[] { "B", "C" })
    {
        Put($"Songs/MIDI/NCN/Song/x/{code}.mid", "midi-" + code);
        Put($"Songs/MIDI/NCN/Lyrics/x/{code}.lyr", "เนื้อ-" + code);
        Put($"Songs/MIDI/NCN/Cursor/x/{code}.cur", "cursor-" + code);
    }
    var fileTable = Fixture.Table();
    Fixture.Add(fileTable, "B", "NCN", "ซ้ำ", "คนเดียว", "เนื้อเดียว");
    Fixture.Add(fileTable, "A", "EMK", "ซ้ำ", "คนเดียว", "เนื้อเดียว");
    Fixture.Add(fileTable, "C", "NCN", "อีกเพลง", "อีกคน", "อีกเนื้อ");
    var dedup = SongSelection.Filter(SongSelection.Eligible(fileTable, true), DuplicateRule.TitleAndLyric, true);
    var plan = Reconstruction.Plan(dedup.Kept, folders, '0', 20000);
    Check(plan.Ready == 2 && plan.Songs.Sum(s => s.Files.Count) == 4, "resolve retained EMK and complete NCN sets from DBF codes");
    Check(plan.Songs.All(s => s.NewCode == "000001"), "separate EMK and NCN numbering starts at 000001");
    var expectedEmk = Path.Combine(extreme, "Songs/MIDI/EMK NCN Mixer Export/EMK/Export/0/000001.emk".Replace('/', Path.DirectorySeparatorChar));
    Check(plan.Songs[0].Files[0].Target == expectedEmk, "reference export directory layout");
    var copy = await Reconstruction.ExecuteAsync(plan, false, null, default);
    Check(copy.Copied == 4 && copy.Moved == 0 && copy.Failed == 0, "copy retained tracks only");
    foreach (var f in plan.Songs.SelectMany(s => s.Files)) Check(File.ReadAllBytes(f.Source).SequenceEqual(File.ReadAllBytes(f.Target)), "copy byte equality and source retained");
    Check(File.Exists(Path.Combine(folders.Ncn, "Song", "x", "B.mid")), "duplicate MIDI untouched by unique copy");
    var repeat = await Reconstruction.ExecuteAsync(plan, false, null, default);
    Check(repeat.Copied == 4 && repeat.Failed == 0, "existing output uses timestamp collision names");
    var logEntries = File.ReadAllLines(repeat.Report).Select(l => JsonDocument.Parse(l)).ToArray();
    var targets = logEntries.Where(j => j.RootElement.GetProperty("action").GetString() == "copied").Select(j => j.RootElement.GetProperty("target").GetString()!).ToArray();
    Check(targets.Length == 4 && targets.All(p => Path.GetFileName(p).Contains('_')), "collision log records timestamp names");
    var ncnNames = targets.Where(p => !p.EndsWith(".emk")).Select(Path.GetFileNameWithoutExtension).Distinct().Count();
    Check(ncnNames == 1, "NCN collision preserves shared MIDI/LYR/CUR code");
    foreach (var entry in logEntries) entry.Dispose();
    var collision = await Reconstruction.ExecuteAsync(plan, false, null, default);
    Check(collision.Failed == 2 && collision.Copied == 0, "existing timestamp collision never overwrites files");

    // Remove output from this isolated fixture and check the move semantics independently.
    Directory.Delete(folders.ExportRoot, true);
    var moved = await Reconstruction.ExecuteAsync(plan, true, null, default);
    Check(moved.Moved == 4 && moved.Failed == 0 && moved.Copied == 0, "move retained tracks and all NCN companions");
    Check(plan.Songs.SelectMany(s => s.Files).All(f => !File.Exists(f.Source) && File.Exists(f.Target)), "moved originals removed and renamed outputs present");
    Check(new[] { "Song/x/B.mid", "Lyrics/x/B.lyr", "Cursor/x/B.cur" }.All(p => File.Exists(Path.Combine(folders.Ncn, p.Replace('/', Path.DirectorySeparatorChar)))), "duplicates remain at source exactly as reference move workflow");
    var stale = await Reconstruction.ExecuteAsync(plan, true, null, default);
    Check(stale.Failed == 2 && stale.Moved == 0, "stale plan cannot move missing originals again");

    var numberTable = Fixture.Table();
    for (int i = 0; i < 5; i++) Fixture.Add(numberTable, "M" + i, "EMK", "T" + i, "A", "L" + i);
    var numberPlan = Reconstruction.Plan(numberTable, folders, '9', 2);
    Check(string.Join(',', numberPlan.Songs.Select(s => s.NewCode)) == "900001,900002,A00001,A00002,B00001", "numbering rollover 9 to A to B");
    Check(numberPlan.Songs.All(s => s.Problems.Count > 0), "missing source files detected before export");
    Check(numberPlan.Songs[1].NewCode == "900002", "missing file still reserves reference numbering slot");
    Throws<InvalidOperationException>(() => Reconstruction.Plan(numberTable, folders, 'Z', 2), "export capacity validated");
    Throws<ArgumentException>(() => Reconstruction.Plan(numberTable, folders, '0', 0), "zero folder count rejected");
    Throws<ArgumentException>(() => Reconstruction.Plan(numberTable, folders, '0', 100000), "over 99999 folder count rejected");
    Throws<InvalidOperationException>(() => Reconstruction.Plan(numberTable, folders with { Emk = extreme }, '0', 20000), "export/source overlap rejected");
    var missing = await Reconstruction.ExecuteAsync(numberPlan, false, null, default);
    Check(missing.Skipped == 5 && missing.Copied == 0, "missing file sets skipped and reported");

    // Missing NCN companions cannot cause a partial destructive move.
    Put("Songs/MIDI/NCN/Song/z/D.mid", "midi-D");
    var incomplete = Fixture.Table(); Fixture.Add(incomplete, "D", "NCN", "D", "D", "D");
    var incompletePlan = Reconstruction.Plan(incomplete, folders, '0', 20000);
    Check(incompletePlan.Missing == 1 && incompletePlan.Songs[0].Problems.Count == 2, "missing NCN lyric and cursor detected");
    var skipped = await Reconstruction.ExecuteAsync(incompletePlan, true, null, default);
    Check(skipped.Skipped == 1 && File.Exists(Path.Combine(folders.Ncn, "Song", "z", "D.mid")), "incomplete NCN set not partially moved");

    Put("Songs/MIDI/EMK/F.emk", "root first"); Put("Songs/MIDI/EMK/other/F.emk", "nested second");
    var duplicatePath = Fixture.Table(); Fixture.Add(duplicatePath, "F", "EMK", "F", "F", "F");
    var direct = Reconstruction.Plan(duplicatePath, folders, '1', 20000);
    Check(direct.Songs[0].Files[0].Source == Path.Combine(folders.Emk, "F.emk"), "reference root file priority in path resolution");
    using var stop = new CancellationTokenSource(); stop.Cancel();
    var cancelledExport = await Reconstruction.ExecuteAsync(direct, false, null, stop.Token);
    Check(cancelledExport.Cancelled && cancelledExport.Copied == 0 && File.Exists(cancelledExport.Report), "cancelled export writes result report");
    Put("Songs/MIDI/EMK/G.emk", "second file"); Fixture.Add(duplicatePath, "G", "EMK", "G", "G", "G");
    var two = Reconstruction.Plan(duplicatePath, folders, '2', 20000);
    using var between = new CancellationTokenSource();
    var afterOne = await Reconstruction.ExecuteAsync(two, false, new DirectProgress(_ => between.Cancel()), between.Token);
    Check(afterOne.Cancelled && afterOne.Copied == 1, "cancellation between songs preserves completed output");
    Check(!Directory.EnumerateFiles(folders.ExportRoot, "*.partial-*", SearchOption.AllDirectories).Any(), "no unfinished temporary output");
    Console.WriteLine($"All {passed} checks passed.");
}
finally { Directory.Delete(temporary, true); }

class DirectProgress(Action<ExportProgress> callback) : IProgress<ExportProgress> { public void Report(ExportProgress value) => callback(value); }
static class ReferenceOracle
{
    // Functional expressions observed in btn_SongFilterByLyric / btn_SongFilterByArtistLyric.
    public static DataTable Select(DataTable input, DuplicateRule mode, bool emkFirst)
    {
        var eligible = new DataView(input) { RowFilter = "TYPE like '%MIDI%' and SUB_TYPE like '%EMK%' or SUB_TYPE like '%NCN%'", Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" }.ToTable();
        var sorted = new DataView(eligible) { Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" }.ToTable();
        IEnumerable<DataRow> selected;
        if (mode == DuplicateRule.TitleAndLyric)
            selected = sorted.AsEnumerable().GroupBy(r => new { Col1 = r["LYR_TITLE"], Col2 = r["TITLE"] }).Select(g => g.OrderBy(r => r["TITLE"]).First());
        else
            selected = sorted.AsEnumerable().GroupBy(r => new { Col1 = r["LYR_TITLE"], Col2 = r["ARTIST"] }).Select(g => g.OrderBy(r => r["ARTIST"]).First());
        var result = sorted.Clone(); foreach (var row in selected) result.ImportRow(row); return result;
    }
}
static class Fixture
{
    public static DataTable Table()
    {
        var t = new DataTable(); foreach (string name in SongSelection.Required) t.Columns.Add(name, typeof(string)); return t;
    }
    public static void Add(DataTable t, string code, string subtype, string title, string artist, string lyric, string type = "MIDI")
    {
        var r = t.NewRow(); r["CODE"] = code; r["TYPE"] = type; r["SUB_TYPE"] = subtype; r["TITLE"] = title; r["ARTIST"] = artist; r["LYR_TITLE"] = lyric; t.Rows.Add(r);
    }
    public static void WriteDbf(string path, DataTable table, int deletedRow = -1)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); var thai = Encoding.GetEncoding(874);
        (string Name, char Type, int Size, int Decimal)[] fields = [("CODE", 'C', 12, 0), ("TYPE", 'C', 8, 0), ("SUB_TYPE", 'C', 8, 0), ("TITLE", 'C', 40, 0), ("ARTIST", 'C', 40, 0), ("LYR_TITLE", 'C', 80, 0), ("TEMPO", 'N', 4, 0), ("RATING", 'N', 6, 2), ("ADDED", 'D', 8, 0), ("ACTIVE", 'L', 1, 0)];
        int headerSize = 32 + fields.Length * 32 + 1; int recordSize = fields.Sum(f => f.Size) + 1;
        using var output = new BinaryWriter(File.Create(path));
        var header = new byte[32]; header[0] = 3; BitConverter.GetBytes(table.Rows.Count).CopyTo(header, 4); BitConverter.GetBytes((ushort)headerSize).CopyTo(header, 8); BitConverter.GetBytes((ushort)recordSize).CopyTo(header, 10); header[29] = 0x7C; output.Write(header);
        foreach (var f in fields) { var d = new byte[32]; Encoding.ASCII.GetBytes(f.Name).CopyTo(d, 0); d[11] = (byte)f.Type; d[16] = (byte)f.Size; d[17] = (byte)f.Decimal; output.Write(d); }
        output.Write((byte)0x0D);
        for (int i = 0; i < table.Rows.Count; i++)
        {
            output.Write((byte)(i == deletedRow ? '*' : ' '));
            foreach (var f in fields)
            {
                var text = f.Name switch { "TEMPO" => " 120", "RATING" => "  1.25", "ADDED" => "20240809", "ACTIVE" => "Y", _ => table.Rows[i][f.Name].ToString()! };
                var bytes = thai.GetBytes(text); var padded = Enumerable.Repeat((byte)' ', f.Size).ToArray(); bytes.AsSpan(0, Math.Min(bytes.Length, padded.Length)).CopyTo(padded); output.Write(padded);
            }
        }
        output.Write((byte)0x1A);
    }
}
