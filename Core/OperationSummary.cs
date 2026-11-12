namespace Mixer.Core;
public record OperationSummary(int Loaded, int Kept, int Duplicates, int ReadySongs, int BlockedSongs, int Files, long Bytes, long? FreeBytes, string Destination, string[] Problems)
{
    public static OperationSummary Create(ExportPlan plan, int loaded, int kept, int duplicates)
    {
        var files = plan.Songs.Where(s => s.Problems.Count == 0).SelectMany(s => s.Files).Select(f => f.Source).ToArray();
        long bytes = 0;
        foreach (var path in files) { try { bytes = checked(bytes + new FileInfo(path).Length); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        long? free = null;
        try { var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(plan.ExportRoot))!); if (drive.IsReady) free = drive.AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(loaded, kept, duplicates, plan.Ready, plan.Missing, files.Length, bytes, free, plan.ExportRoot,
            plan.Songs.Where(s => s.Problems.Count > 0).Select(s => s.Kind + " " + s.OldCode + ": " + string.Join("; ", s.Problems)).ToArray());
    }
    public static string Size(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824.0:N2} GB" : bytes >= 1048576 ? $"{bytes / 1048576.0:N2} MB" : bytes >= 1024 ? $"{bytes / 1024.0:N1} KB" : $"{bytes:N0} B";
}
