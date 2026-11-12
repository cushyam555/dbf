using System.Data;
using System.Text.Json;

namespace Mixer.Core;
public enum DuplicateRule { TitleAndLyric, ArtistAndLyric }
public record SelectionResult(DataTable Kept, DataTable Duplicates, DataTable Review, int OriginalCount, int GroupCount);
public static class SongSelection
{
    public const string RowId = "__MixerStudioRowId";
    public static string GroupKey(DataRow row, DuplicateRule rule) => JsonSerializer.Serialize(new[] { rule.ToString(), row.IsNull("LYR_TITLE") ? null : row["LYR_TITLE"].ToString(), row.IsNull(rule == DuplicateRule.TitleAndLyric ? "TITLE" : "ARTIST") ? null : row[rule == DuplicateRule.TitleAndLyric ? "TITLE" : "ARTIST"].ToString() });
    public static readonly string[] Required = ["CODE", "TYPE", "SUB_TYPE", "TITLE", "ARTIST", "LYR_TITLE"];
    public static DataTable Load(string path, bool emkFirst, CancellationToken token = default) => Eligible(DbfReader.Read(path, token), emkFirst);
    public static DataTable Eligible(DataTable source, bool emkFirst)
    {
        foreach (var name in Required)
            if (!source.Columns.Contains(name)) throw new InvalidDataException($"Song.dbf ไม่มีคอลัมน์ {name}");
        // Keep the reference's AND/OR precedence, including NCN records regardless of TYPE.
        var view = new DataView(source) { RowFilter = "TYPE like '%MIDI%' and SUB_TYPE like '%EMK%' or SUB_TYPE like '%NCN%'", Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" };
        var result = view.ToTable("Eligible"); EnsureIds(result); return result;
    }
    internal static void EnsureIds(DataTable table) { if (!table.Columns.Contains(RowId)) { table.Columns.Add(RowId, typeof(int)); for (int i = 0; i < table.Rows.Count; i++) table.Rows[i][RowId] = i; } }
    public static SelectionResult Filter(DataTable all, DuplicateRule rule, bool emkFirst, CancellationToken token = default, IReadOnlyDictionary<string, int>? choices = null)
    {
        EnsureIds(all);
        var sorted = new DataView(all) { Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" }.ToTable();
        var kept = all.Clone(); var duplicates = all.Clone(); var review = all.Clone();
        review.Columns.Add("เหตุผล", typeof(string)); review.Columns.Add("จำนวนในกลุ่ม", typeof(int)); review.Columns.Add("ผลการคัด", typeof(string)); review.Columns.Add("กลุ่มซ้ำ", typeof(int)); review.Columns.Add("เก็บรหัส", typeof(string));
        // Keys retain exact field values: no trimming, case-folding, punctuation removal or filename hashing.
        var keys = new Dictionary<(object Lyric, object Other), (DataRow Winner, int Number, int Count)>();
        var column = rule == DuplicateRule.TitleAndLyric ? "TITLE" : "ARTIST";
        foreach (DataRow row in sorted.Rows)
        {
            token.ThrowIfCancellationRequested();
            var key = (row["LYR_TITLE"], row[column]);
            if (keys.TryGetValue(key, out var found)) keys[key] = (found.Winner, found.Number, found.Count + 1);
            else keys.Add(key, (row, keys.Count + 1, 1));
        }
        if (choices != null)
            foreach (DataRow row in sorted.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (choices.TryGetValue(GroupKey(row, rule), out int winner) && winner == (int)row[RowId])
                {
                    var key = (row["LYR_TITLE"], row[column]); var current = keys[key]; keys[key] = (row, current.Number, current.Count);
                }
            }
        if (choices != null)
            foreach (var group in keys.Values)
                if (choices.TryGetValue(GroupKey(group.Winner, rule), out int selectedId) && (int)group.Winner[RowId] != selectedId)
                    throw new InvalidDataException("ตัวเลือกเพลงที่เก็บไม่ตรงกับข้อมูลกลุ่ม กรุณาคัดเลือกใหม่");
        foreach (DataRow row in sorted.Rows)
        {
            token.ThrowIfCancellationRequested();
            var group = keys[(row["LYR_TITLE"], row[column])];
            bool keep = ReferenceEquals(row, group.Winner);
            (keep ? kept : duplicates).ImportRow(row);
            var entry = review.NewRow();
            foreach (DataColumn col in all.Columns) entry[col.ColumnName] = row[col.ColumnName];
            entry["เหตุผล"] = group.Count == 1 ? "พบเพียงรายการเดียว" : (rule == DuplicateRule.TitleAndLyric ? "ชื่อเพลง + เนื้อเพลงตรงกัน" : "นักร้อง + เนื้อเพลงตรงกัน") + (choices?.ContainsKey(GroupKey(row, rule)) == true ? " • เลือกผู้เก็บเอง" : "");
            entry["จำนวนในกลุ่ม"] = group.Count;
            entry["ผลการคัด"] = keep ? "เก็บ" : "ซ้ำ";
            entry["กลุ่มซ้ำ"] = group.Number; entry["เก็บรหัส"] = group.Winner["CODE"].ToString()!.Trim();
            review.Rows.Add(entry);
        }
        return new(kept, duplicates, review, all.Rows.Count, keys.Count(x => x.Value.Count > 1));
    }
}
