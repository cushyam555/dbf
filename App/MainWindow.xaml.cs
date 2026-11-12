using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
using Mixer.Core;

namespace MixerStudio;
public class SongRow(Song song) : INotifyPropertyChanged
{
    public Song Song { get; } = song;
    public string Code => Song.Code;
    public string Kind => Song.Kind;
    public string Status => Song.Status;
    public string RelativePath => Song.RelativePath;
    public bool HasIssue => Status.Contains("ขาด") || Status.Contains("ซ้ำ") || Status.Contains("ไม่ได้");
    bool selected;
    public bool Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public partial class MainWindow : Window
{
    readonly ObservableCollection<SongRow> rows = [];
    ScanResult? scan;
    CancellationTokenSource? job;
    bool busy;
    public MainWindow()
    {
        InitializeComponent();
        SongsGrid.ItemsSource = rows;
        CollectionViewSource.GetDefaultView(rows).Filter = Match;
        Search.ToolTip = "ค้นหารหัสเพลง ชื่อไฟล์ หรือโฟลเดอร์";
    }
    void ChooseSource(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "เลือกโฟลเดอร์คลังเพลง" };
        if (picker.ShowDialog(this) == true)
        {
            Source.Text = picker.FolderName; scan = null; rows.Clear(); UpdateCounts();
            StatusText.Text = "เลือกต้นทางแล้ว • กดสแกนไฟล์เพื่อตรวจสอบ"; Empty.Visibility = Visibility.Visible;
        }
    }
    void ChooseDestination(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "เลือกโฟลเดอร์ปลายทางที่แยกจากต้นทาง" };
        if (picker.ShowDialog(this) == true) Destination.Text = picker.FolderName;
        UpdateCounts();
    }
    async void ScanClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Source.Text)) { MessageBox.Show(this, "กรุณาเลือกโฟลเดอร์ต้นทางก่อน", "เลือกโฟลเดอร์"); return; }
        var source = Source.Text;
        StartJob("กำลังค้นหาไฟล์ในโฟลเดอร์…"); Progress.IsIndeterminate = true;
        try
        {
            var result = await Task.Run(() => Library.Scan(source, job!.Token));
            scan = result; rows.Clear();
            foreach (var song in result.Songs)
            {
                var row = new SongRow(song);
                row.PropertyChanged += (_, _) => UpdateCounts(); rows.Add(row);
            }
            Empty.Text = "ไม่พบไฟล์เพลงในโฟลเดอร์นี้";
            StatusText.Text = $"สแกนเสร็จ {rows.Count:N0} เพลง • ข้อสังเกต {result.Warnings.Count:N0} รายการ";
            if (result.Warnings.Count > 0)
                MessageBox.Show(this, string.Join("\n", result.Warnings.Take(12)) + (result.Warnings.Count > 12 ? "\n…ยังมีรายการเพิ่มเติม" : ""), "ข้อสังเกตจากการสแกน");
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดสแกนแล้ว"; }
        catch (Exception ex) { ShowError(ex); }
        finally { EndJob(); ApplyFilter(); }
    }
    bool Match(object item)
    {
        var row = (SongRow)item;
        var text = Search?.Text?.Trim() ?? "";
        return (OnlyIssues?.IsChecked != true || row.HasIssue) && (text.Length == 0 || row.Code.Contains(text, StringComparison.OrdinalIgnoreCase) || row.RelativePath.Contains(text, StringComparison.OrdinalIgnoreCase));
    }
    void FilterChanged(object sender, RoutedEventArgs e) { if (SongsGrid != null) ApplyFilter(); }
    void ApplyFilter()
    {
        CollectionViewSource.GetDefaultView(rows).Refresh();
        Empty.Visibility = CollectionViewSource.GetDefaultView(rows).IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count > 0) Empty.Text = "ไม่มีรายการตรงกับตัวกรอง";
    }
    void SelectVisible(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var visible = CollectionViewSource.GetDefaultView(rows).Cast<SongRow>().ToArray();
        var select = !visible.All(r => r.Selected);
        foreach (var row in visible) row.Selected = select;
        UpdateCounts();
    }
    void UpdateCounts()
    {
        TotalText.Text = rows.Count.ToString("N0"); IssuesText.Text = rows.Count(r => r.HasIssue).ToString("N0");
        SelectedText.Text = rows.Count(r => r.Selected).ToString("N0");
        ExportButton.IsEnabled = !busy && scan != null && rows.Any(r => r.Selected) && Destination.Text.Length > 0;
    }
    async void ExportClick(object sender, RoutedEventArgs e)
    {
        SongsGrid.CommitEdit();
        if (scan == null) return;
        var selected = rows.Where(r => r.Selected).ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(r => r.HasIssue))
        {
            MessageBox.Show(this, "รายการที่เลือกมีไฟล์ประกอบขาดหรือซ้ำ กรุณาตรวจสอบและยกเลิกเลือกแถวเหล่านั้นก่อนคัดลอก", "ตรวจสอบไฟล์ก่อน"); return;
        }
        var destination = Destination.Text;
        var currentScan = scan;
        StartJob("กำลังคัดลอกไฟล์…");
        try
        {
            var progress = new Progress<(int Done, int Total, string File)>(p =>
            {
                Progress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                StatusText.Text = $"{p.Done:N0} / {p.Total:N0} ไฟล์ • {p.File}";
            });
            var result = await Task.Run(() => Library.ExportAsync(currentScan, selected.Select(r => r.Song), destination, progress, job!.Token));
            StatusText.Text = $"{(result.Cancelled ? "หยุดแล้ว" : "เสร็จแล้ว")} • คัดลอก {result.Copied} • ข้าม {result.Skipped} • ผิดพลาด {result.Failed}";
            MessageBox.Show(this, $"{StatusText.Text}\n\nบันทึกไว้ที่:\n{result.Folder}\n\nดูรายละเอียดใน export-report.json", "ผลการคัดลอก");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndJob(); }
    }
    void StartJob(string message)
    {
        busy = true; job = new(); StatusText.Text = message; Progress.Value = 0;
        BrowseSource.IsEnabled = ScanButton.IsEnabled = BrowseDest.IsEnabled = SongsGrid.IsEnabled = false;
        CancelButton.IsEnabled = true; UpdateCounts();
    }
    void EndJob()
    {
        busy = false; job?.Dispose(); job = null; Progress.IsIndeterminate = false;
        BrowseSource.IsEnabled = ScanButton.IsEnabled = BrowseDest.IsEnabled = SongsGrid.IsEnabled = true;
        CancelButton.IsEnabled = false; UpdateCounts();
    }
    void CancelClick(object sender, RoutedEventArgs e) { job?.Cancel(); CancelButton.IsEnabled = false; StatusText.Text = "กำลังหยุดและบันทึกผล…"; }
    void ShowError(Exception ex) { StatusText.Text = "ทำงานไม่สำเร็จ"; MessageBox.Show(this, ex.Message, "ทำงานไม่สำเร็จ", MessageBoxButton.OK, MessageBoxImage.Warning); }
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; job?.Cancel(); StatusText.Text = "กำลังหยุดงาน • เมื่อหยุดแล้วสามารถปิดหน้าต่างได้"; }
    }
}
