using System.ComponentModel;
using System.Data;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using Mixer.Core;

namespace MixerStudio;
public partial class MainWindow : Window
{
    DataTable? all;
    SelectionResult? selection;
    DuplicateRule? rule;
    CancellationTokenSource? job;
    bool busy, loaded, mustReload;
    readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MixerStudio", "settings.json");
    DataTable? Kept => selection?.Kept ?? all;
    internal Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult>? DialogOverride { get; set; }
    MessageBoxResult Dialog(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => DialogOverride != null ? DialogOverride(message, title, buttons, image, defaultResult) : MessageBox.Show(this, message, title, buttons, image, defaultResult);

    public MainWindow()
    {
        InitializeComponent();
        StartLetter.ItemsSource = Reconstruction.Alphabet.Select(c => c.ToString()).ToArray(); StartLetter.SelectedIndex = 0;
        try
        {
            if (File.Exists(settings)) SetFolders(JsonSerializer.Deserialize<SourceFolders>(File.ReadAllText(settings))!);
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Company\App");
                var previous = key?.GetValue("Folder_Extreme") as string;
                if (!string.IsNullOrWhiteSpace(previous)) SetFolders(SourceFolders.FromExtreme(previous));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { StatusText.Text = "อ่านการตั้งค่าเดิมไม่ได้ • เลือกโฟลเดอร์ใหม่ได้"; }
        loaded = true;
    }
    SourceFolders Folders => new(ExtremePath.Text, EmkPath.Text, NcnPath.Text);
    internal void SetFolders(SourceFolders folders)
    {
        if (folders == null) return;
        ExtremePath.Text = folders.Extreme; EmkPath.Text = folders.Emk; NcnPath.Text = folders.Ncn;
        ExportPath.Text = "ปลายทาง: " + folders.ExportRoot;
    }
    void SaveFolders()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settings)!); File.WriteAllText(settings, JsonSerializer.Serialize(Folders)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "ใช้โฟลเดอร์ที่เลือกได้ แต่บันทึกการตั้งค่าไม่ได้: " + ex.Message; }
        ExportPath.Text = "ปลายทาง: " + Folders.ExportRoot;
    }
    string? Pick(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    void ChooseExtreme(object sender, RoutedEventArgs e)
    {
        var path = Pick("เลือกโฟลเดอร์ Extreme Karaoke", ExtremePath.Text);
        if (path != null) { SetFolders(SourceFolders.FromExtreme(path)); SaveFolders(); }
    }
    void ChooseEmk(object sender, RoutedEventArgs e) { var path = Pick("เลือกโฟลเดอร์เพลง EMK", EmkPath.Text); if (path != null) { EmkPath.Text = path; SaveFolders(); } }
    void ChooseNcn(object sender, RoutedEventArgs e) { var path = Pick("เลือกโฟลเดอร์ NCN ที่มี Song, Lyrics, Cursor", NcnPath.Text); if (path != null) { NcnPath.Text = path; SaveFolders(); } }
    async void OpenDatabase(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "เปิดฐานข้อมูล Song.dbf", Filter = "DBF (*.dbf)|*.dbf", FileName = "song.dbf" };
        var initial = Path.Combine(ExtremePath.Text, "Data");
        if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
        if (dialog.ShowDialog(this) != true) return;
        await LoadDatabaseFile(dialog.FileName);
    }
    internal async Task LoadDatabaseFile(string fileName)
    {
        var priority = EmkFirst.IsChecked == true;
        Start("กำลังอ่าน Song.dbf…"); Progress.IsIndeterminate = true;
        try
        {
            var table = await Task.Run(() => SongSelection.Load(fileName, priority, job!.Token));
            all = table; selection = null; rule = null; mustReload = false;
            DatabasePath.Text = fileName;
            StatusText.Text = $"อ่านฐานข้อมูลแล้ว {all.Rows.Count:N0} เพลง • เลือกเกณฑ์คัดซ้ำได้";
            ViewMode.SelectedIndex = 0; RefreshView();
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดอ่านฐานข้อมูลแล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    async void FilterTitle(object sender, RoutedEventArgs e) => await ApplyRule(DuplicateRule.TitleAndLyric);
    async void FilterArtist(object sender, RoutedEventArgs e) => await ApplyRule(DuplicateRule.ArtistAndLyric);
    async void PriorityChanged(object sender, RoutedEventArgs e)
    {
        if (!loaded || busy || all == null) return;
        if (rule != null) await ApplyRule(rule.Value);
        else { all = SongSelection.Eligible(all, EmkFirst.IsChecked == true); RefreshView(); }
    }
    async Task ApplyRule(DuplicateRule chosen)
    {
        if (busy || all == null || mustReload) return;
        SongsGrid.CommitEdit(); SongsGrid.CommitEdit();
        var source = all; bool emkFirst = EmkFirst.IsChecked == true;
        Start("กำลังคัดเพลงซ้ำตามฐานข้อมูล…"); Progress.IsIndeterminate = true;
        try
        {
            selection = await Task.Run(() => SongSelection.Filter(source, chosen, emkFirst, job!.Token)); rule = chosen;
            ViewMode.SelectedIndex = 0; RefreshView();
            StatusText.Text = $"คัดเหลือ {selection.Kept.Rows.Count:N0} เพลง • ซ้ำ {selection.Duplicates.Rows.Count:N0} เพลง ({selection.GroupCount:N0} กลุ่ม)";
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดคัดเพลงแล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    void ResetFilter(object sender, RoutedEventArgs e)
    {
        if (busy || mustReload) return;
        selection = null; rule = null; ViewMode.SelectedIndex = 0; RefreshView(); StatusText.Text = "คืนรายการทั้งหมดจากฐานข้อมูลแล้ว • ยังไม่ได้คัดซ้ำ";
    }
    void ViewChanged(object sender, SelectionChangedEventArgs e) { if (loaded) RefreshView(); }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (loaded) FilterView(); }
    void RefreshView()
    {
        DataTable? displayed = ViewMode.SelectedIndex switch { 1 => selection?.Duplicates, 2 => selection?.Review, _ => Kept };
        if (displayed != null) displayed.DefaultView.RowFilter = "";
        SongsGrid.ItemsSource = displayed?.DefaultView;
        SongsGrid.IsReadOnly = ViewMode.SelectedIndex != 0 || mustReload;
        TotalText.Text = (all?.Rows.Count ?? 0).ToString("N0"); KeptText.Text = (Kept?.Rows.Count ?? 0).ToString("N0"); DuplicateText.Text = (selection?.Duplicates.Rows.Count ?? 0).ToString("N0");
        int Count(string kind) => Kept == null ? 0 : new DataView(Kept) { RowFilter = $"SUB_TYPE like '{kind}'" }.Count;
        TypeCount.Text = $"EMK {Count("EMK"):N0} · NCN {Count("NCN"):N0}";
        FilterView(); UpdateButtons();
    }
    void FilterView()
    {
        if (SongsGrid.ItemsSource is DataView view)
        {
            var text = Search.Text.Trim();
            if (text.Length == 0) view.RowFilter = "";
            else
            {
                // Escape DataColumn expression metacharacters; search never changes the export selection.
                string literal = string.Concat(text.Select(c => c switch { '\'' => "''", '[' => "[[]", ']' => "[]]", '%' => "[%]", '*' => "[*]", _ => c.ToString() }));
                view.RowFilter = string.Join(" OR ", new[] { "CODE", "TITLE", "ARTIST", "LYR_TITLE" }.Select(c => $"CONVERT([{c}], 'System.String') LIKE '%{literal}%'"));
            }
            Empty.Visibility = view.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Empty.Text = "ไม่มีรายการตรงกับมุมมองหรือตัวกรองนี้";
        }
        else { Empty.Text = all == null ? "เปิด Song.dbf เพื่อเริ่มคัดเพลงซ้ำ" : "เลือกเกณฑ์คัดซ้ำก่อนดูรายการนี้"; Empty.Visibility = Visibility.Visible; }
    }
    void GridColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CODE"] = "รหัสเพลง", ["TITLE"] = "ชื่อเพลง", ["ARTIST"] = "นักร้อง", ["LYR_TITLE"] = "เนื้อเพลง", ["SUB_TYPE"] = "EMK / NCN", ["TYPE"] = "ประเภท" };
        e.Column.Header = headers.GetValueOrDefault(e.PropertyName, e.PropertyName);
        e.Column.MinWidth = 90; e.Column.Width = e.PropertyName is "TITLE" or "LYR_TITLE" ? 220 : e.PropertyName == "ARTIST" ? 160 : 110;
        if (e.Column is DataGridBoundColumn column)
            column.Binding = new Binding(e.PropertyName) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
    }
    async Task<ExportPlan?> Prepare()
    {
        if (Kept == null || Kept.Rows.Count == 0 || mustReload) return null;
        SongsGrid.CommitEdit(); SongsGrid.CommitEdit();
        if (ExtremePath.Text.Length == 0 || EmkPath.Text.Length == 0 || NcnPath.Text.Length == 0) throw new InvalidOperationException("เลือกโฟลเดอร์ Extreme, EMK และ NCN ให้ครบก่อน");
        if (!int.TryParse(PerFolder.Text, out var count)) throw new InvalidOperationException("กรอกจำนวนเพลงต่อโฟลเดอร์เป็นตัวเลข 1–99,999");
        var folders = Folders; var first = StartLetter.SelectedItem?.ToString()?[0] ?? '0'; var kept = Kept;
        return await Task.Run(() => Reconstruction.Plan(kept, folders, first, count, job!.Token));
    }
    async void PreviewExport(object sender, RoutedEventArgs e)
    {
        if (busy) return; Start("กำลังตรวจไฟล์และวางแผนส่งออก…"); Progress.IsIndeterminate = true;
        try
        {
            var plan = await Prepare();
            if (plan == null) { StatusText.Text = "ยังไม่มีเพลงที่จะส่งออก"; return; }
            Dialog($"รายการ {plan.Songs.Count:N0} เพลง\nพร้อมส่งออก {plan.Ready:N0} เพลง\nไฟล์ขาด / ต้องตรวจสอบ {plan.Missing:N0} เพลง\n\nปลายทาง:\n{plan.ExportRoot}\n\n" + string.Join("\n", plan.Songs.Where(s => s.Problems.Count > 0).Take(8).Select(s => s.Kind + " " + s.OldCode + ": " + string.Join("; ", s.Problems))), "ตรวจไฟล์ก่อนส่งออก");
            StatusText.Text = $"ตรวจแล้ว • พร้อม {plan.Ready:N0} • ต้องตรวจสอบ {plan.Missing:N0} เพลง";
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดตรวจไฟล์แล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    async void CopyClick(object sender, RoutedEventArgs e) => await Export(false);
    async void MoveClick(object sender, RoutedEventArgs e) => await Export(true);
    async Task Export(bool move)
    {
        if (busy || mustReload || Kept == null || Kept.Rows.Count == 0) return;
        if (move)
        {
            if (Dialog($"จะย้ายเพลงที่เก็บไว้ {Kept.Rows.Count:N0} เพลงไปยัง Export และเปลี่ยนรหัสใหม่\nไฟล์เหล่านี้จะออกจากต้นทาง ส่วนเพลงซ้ำที่คัดออกยังอยู่ต้นทาง\n\n{Folders.ExportRoot}\n\nต้องการดำเนินการหรือไม่?", "ยืนยันการย้ายเพลง", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Dialog("ยืนยันอีกครั้ง: รหัสเพลงจะเปลี่ยน และต้องจัดทำฐานข้อมูลใน Extreme ใหม่หลังย้าย\nดำเนินการต่อหรือไม่?", "ยืนยันการเปลี่ยนรหัส", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        Start("กำลังตรวจไฟล์ก่อนส่งออก…"); Progress.IsIndeterminate = true;
        try
        {
            var plan = await Prepare(); if (plan == null) return;
            if (plan.Ready == 0) throw new InvalidOperationException("ไม่พบชุดไฟล์ที่พร้อมส่งออก • กดตรวจไฟล์ก่อนส่งออกเพื่อดูรายละเอียด");
            if (plan.Missing > 0 && Dialog($"มีชุดไฟล์ไม่ครบ {plan.Missing:N0} เพลง จะข้ามรายการเหล่านี้\nส่งออกเฉพาะ {plan.Ready:N0} เพลงที่ครบหรือไม่?", "พบไฟล์ขาด", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { StatusText.Text = "ยกเลิกส่งออกแล้ว"; return; }
            Progress.IsIndeterminate = false;
            var progress = new Progress<ExportProgress>(p => { Progress.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total; StatusText.Text = $"{(move ? "ย้าย" : "สำเนา")} {p.Done:N0}/{p.Total:N0} เพลง • {p.Kind} {p.Code}"; ProgressText.Text = $"EMK: {p.EmkDone:N0} · NCN: {p.NcnDone:N0}"; });
            // Any interrupted move may already have changed files: require a fresh DBF before another run.
            if (move) mustReload = true;
            var result = await Task.Run(() => Reconstruction.ExecuteAsync(plan, move, progress, job!.Token));
            StatusText.Text = $"{(result.Cancelled ? "หยุดแล้ว" : "เสร็จแล้ว")} • สำเนา {result.Copied:N0} / ย้าย {result.Moved:N0} ไฟล์ • ข้าม {result.Skipped:N0} / ผิดพลาด {result.Failed:N0} เพลง";
            Dialog(StatusText.Text + "\n\nรายงาน:\n" + result.Report + (move ? "\n\nกรุณาจัดทำฐานข้อมูล Extreme ใหม่ แล้วเปิด Song.dbf ใหม่ก่อนทำงานต่อ" : ""), "ผลการส่งออก");
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดตรวจไฟล์แล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    void Start(string text)
    {
        busy = true; job = new(); StatusText.Text = text; Progress.Value = 0;
        FoldersPanel.IsEnabled = FilterPanel.IsEnabled = OptionsPanel.IsEnabled = SongsGrid.IsEnabled = false;
        StopButton.IsEnabled = true; UpdateButtons();
    }
    void End()
    {
        busy = false; job?.Dispose(); job = null; Progress.IsIndeterminate = false;
        FoldersPanel.IsEnabled = FilterPanel.IsEnabled = OptionsPanel.IsEnabled = SongsGrid.IsEnabled = true;
        StopButton.IsEnabled = false; UpdateButtons();
        if (mustReload) { SongsGrid.IsReadOnly = true; StatusText.Text += " • เปิดฐานข้อมูลใหม่ก่อนทำงานต่อ"; }
    }
    void UpdateButtons() { CopyButton.IsEnabled = MoveButton.IsEnabled = !busy && !mustReload && Kept?.Rows.Count > 0; }
    void StopClick(object sender, RoutedEventArgs e) { job?.Cancel(); StopButton.IsEnabled = false; StatusText.Text = "กำลังหยุด • จะทำชุดไฟล์เพลงปัจจุบันให้เสร็จก่อน"; }
    void Error(Exception ex) { StatusText.Text = "ทำงานไม่สำเร็จ"; Dialog(ex.Message, "ทำงานไม่สำเร็จ", MessageBoxButton.OK, MessageBoxImage.Warning); }
    void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; job?.Cancel(); StatusText.Text = "กำลังหยุดงาน • เมื่อหยุดแล้วสามารถปิดหน้าต่างได้"; } }
}
