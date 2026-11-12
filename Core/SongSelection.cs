using System.Data;

namespace Mixer.Core;
public enum DuplicateRule { TitleAndLyric, ArtistAndLyric }
public record SelectionResult(DataTable Kept, DataTable Duplicates, DataTable Review, int OriginalCount, int GroupCount);
public static class SongSelection
{
    public static readonly string[] Required = ["CODE", "TYPE", "SUB_TYPE", "TITLE", "ARTIST", "LYR_TITLE"];
    public static DataTable Load(string path, bool emkFirst, CancellationToken token = default) => Eligible(DbfReader.Read(path, token), emkFirst);
    public static DataTable Eligible(DataTable source, bool emkFirst)
    {
        foreach (var name in Required)
            if (!source.Columns.Contains(name)) throw new InvalidDataException($"Song.dbf ไม่มีคอลัมน์ {name}");
        // Keep the reference's AND/OR precedence, including NCN records regardless of TYPE.
        var view = new DataView(source) { RowFilter = "TYPE like '%MIDI%' and SUB_TYPE like '%EMK%' or SUB_TYPE like '%NCN%'", Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" };
        return view.ToTable("Eligible");
    }
    public static SelectionResult Filter(DataTable all, DuplicateRule rule, bool emkFirst, CancellationToken token = default)
    {
        var sorted = new DataView(all) { Sort = emkFirst ? "SUB_TYPE" : "SUB_TYPE DESC" }.ToTable();
        var kept = all.Clone(); var duplicates = all.Clone(); var review = all.Clone();
        review.Columns.Add("ผลการคัด", typeof(string)); review.Columns.Add("กลุ่มซ้ำ", typeof(int)); review.Columns.Add("เก็บรหัส", typeof(string));
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
        foreach (DataRow row in sorted.Rows)
        {
            token.ThrowIfCancellationRequested();
            var group = keys[(row["LYR_TITLE"], row[column])];
            bool keep = ReferenceEquals(row, group.Winner);
            (keep ? kept : duplicates).ImportRow(row);
            var entry = review.NewRow();
            foreach (DataColumn col in all.Columns) entry[col.ColumnName] = row[col.ColumnName];
            entry["ผลการคัด"] = keep ? "เก็บ" : "ซ้ำ";
            entry["กลุ่มซ้ำ"] = group.Number; entry["เก็บรหัส"] = group.Winner["CODE"].ToString()!.Trim();
            review.Rows.Add(entry);
        }
        return new(kept, duplicates, review, all.Rows.Count, keys.Count(x => x.Value.Count > 1));
    }
}
