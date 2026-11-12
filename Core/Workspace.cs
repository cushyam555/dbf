using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
namespace Mixer.Core;
public record WorkspaceInfo(int Version, SourceFolders Folders, string Database, string? DatabaseHash, DuplicateRule? Rule, bool EmkFirst, char Start, int PerFolder, bool Unlimited, Dictionary<string, int> Choices, bool MustReload, string? LastHold);
public record SavedWorkspace(WorkspaceInfo Info, DataTable Songs);
public static class Workspace
{
    public static string HashFile(string path) { using var f = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(f)); }
    public static void Save(string path, WorkspaceInfo info, DataTable songs)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                using (var output = zip.CreateEntry("workspace.json").Open()) JsonSerializer.Serialize(output, info);
                using var data = zip.CreateEntry("songs.xml", CompressionLevel.Optimal).Open(); songs.WriteXml(data, XmlWriteMode.WriteSchema);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static SavedWorkspace Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var input = (zip.GetEntry("workspace.json") ?? throw new InvalidDataException("ไฟล์งานไม่มีข้อมูลตั้งค่า")).Open();
        var info = JsonSerializer.Deserialize<WorkspaceInfo>(input) ?? throw new InvalidDataException("ข้อมูลไฟล์งานไม่ถูกต้อง");
        if (info.Version != 1 || !Reconstruction.Alphabet.Contains(info.Start) || info.PerFolder is < 1 or > 99999 || info.Choices == null || info.Folders == null || info.Folders.Extreme == null || info.Folders.Emk == null || info.Folders.Ncn == null || info.Database == null || (info.Rule.HasValue && !Enum.IsDefined(info.Rule.Value))) throw new InvalidDataException("รุ่นหรือค่าตั้งในไฟล์งานไม่รองรับ");
        using var data = (zip.GetEntry("songs.xml") ?? throw new InvalidDataException("ไฟล์งานไม่มีรายการเพลง")).Open();
        using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var table = new DataTable(); table.ReadXml(reader);
        foreach (var column in SongSelection.Required) if (!table.Columns.Contains(column)) throw new InvalidDataException("ไฟล์งานไม่มีคอลัมน์ " + column);
        if (table.Columns.Contains(SongSelection.RowId) && (table.Columns[SongSelection.RowId]!.DataType != typeof(int) || table.Rows.Cast<DataRow>().Any(r => r.IsNull(SongSelection.RowId)) || table.Rows.Cast<DataRow>().Select(r => (int)r[SongSelection.RowId]).Distinct().Count() != table.Rows.Count)) throw new InvalidDataException("รหัสแถวในไฟล์งานไม่ถูกต้อง");
        SongSelection.EnsureIds(table); return new(info, table);
    }
}
