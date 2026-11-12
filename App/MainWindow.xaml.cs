using System.ComponentModel;
using System.Data;
using System.IO;
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
    Dictionary<string, int> choices = new();
    string? databaseHash, lastHold;
    DataTable? Kept => selection?.Kept ?? all;
    internal Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult>? DialogOverride { get; set; }
    MessageBoxResult Dialog(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => DialogOverride != null ? DialogOverride(message, title, buttons, image, defaultResult) : MessageBox.Show(this, message, title, buttons, image, defaultResult);

    internal Func<string, OperationSummary, ExportPlan, bool>? SummaryOverride { get; set; }
    internal Func<GroupsWindow, bool>? GroupsOverride { get; set; }
    bool ShowSummary(string action, string note, ExportPlan plan, OperationSummary summary, bool run)
    {
        if (SummaryOverride != null) return SummaryOverride(action, summary, plan) && run && summary.ReadySongs > 0;
        return new SummaryWindow(action, note, summary, plan, run) { Owner = this }.ShowDialog() == true;
    }
    public MainWindow()
    {
        InitializeComponent();
        StartLetter.ItemsSource = Reconstruction.Alphabet.Select(c => c.ToString()).ToArray(); StartLetter.SelectedIndex = 0;
        loaded = true;
    }
    SourceFolders Folders => new(ExtremePath.Text, EmkPath.Text, NcnPath.Text);
    internal void SetFolders(SourceFolders folders)
    {
        if (folders == null) return;
        ExtremePath.Text = folders.Extreme; EmkPath.Text = folders.Emk; NcnPath.Text = folders.Ncn;
        UpdateExportPath();
    }
    void UpdateExportPath() => ExportPath.Text = string.IsNullOrWhiteSpace(ExtremePath.Text) ? "ปลายทาง: เลือกโฟลเดอร์ Extreme ก่อน" : "ปลายทาง: " + Folders.ExportRoot;
    string? Pick(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    void ChooseExtreme(object sender, RoutedEventArgs e)
    {
        var path = Pick("เลือกโฟลเดอร์ Extreme Karaoke", ExtremePath.Text);
        if (path != null) SelectExtremePath(path);
    }
    internal void SelectExtremePath(string path)
    {
        SetFolders(SourceFolders.FromExtreme(path));
        var missing = new[] { ("EMK", EmkPath.Text), ("NCN", NcnPath.Text) }.Where(x => !Directory.Exists(x.Item2)).Select(x => x.Item1).ToArray();
        StatusText.Text = missing.Length == 0 ? "เติมโฟลเดอร์ EMK / NCN แล้ว • เปลี่ยนแยกได้" : "เติมพาธแล้ว แต่ไม่พบโฟลเดอร์ " + string.Join(" / ", missing) + " • เลือกเปลี่ยนได้";
        UpdateButtons();
    }
    void ChooseEmk(object sender, RoutedEventArgs e) { var path = Pick("เลือกโฟลเดอร์เพลง EMK", EmkPath.Text); if (path != null) { EmkPath.Text = path; UpdateExportPath(); UpdateButtons(); } }
    void ChooseNcn(object sender, RoutedEventArgs e) { var path = Pick("เลือกโฟลเดอร์ NCN ที่มี Song, Lyrics, Cursor", NcnPath.Text); if (path != null) { NcnPath.Text = path; UpdateExportPath(); UpdateButtons(); } }
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
            var data = await Task.Run(() => (Table: SongSelection.Load(fileName, priority, job!.Token), Hash: Workspace.HashFile(fileName)));
            all = data.Table; databaseHash = data.Hash; choices.Clear(); selection = null; rule = null; mustReload = false;
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
            selection = await Task.Run(() => SongSelection.Filter(source, chosen, emkFirst, job!.Token, choices)); rule = chosen;
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
        selection = null; rule = null; choices.Clear(); ViewMode.SelectedIndex = 0; RefreshView(); StatusText.Text = "คืนรายการทั้งหมดจากฐานข้อมูลแล้ว • ยังไม่ได้คัดซ้ำ";
    }
    void ViewChanged(object sender, SelectionChangedEventArgs e) { if (loaded) RefreshView(); }
    void SearchChanged(object sender, TextChangedEventArgs e) { if (loaded) FilterView(); }
    void RefreshView()
    {
        DataTable? displayed = ViewMode.SelectedIndex switch { 1 => selection?.Duplicates, 2 => selection?.Review, _ => Kept };
        if (displayed != null) displayed.DefaultView.RowFilter = "";
        SongsGrid.ItemsSource = displayed?.DefaultView;
        SongsGrid.IsReadOnly = true;
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
        if (e.PropertyName == SongSelection.RowId) { e.Cancel = true; return; }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CODE"] = "รหัสเพลง", ["TITLE"] = "ชื่อเพลง", ["ARTIST"] = "นักร้อง", ["LYR_TITLE"] = "เนื้อเพลง", ["SUB_TYPE"] = "EMK / NCN", ["TYPE"] = "ประเภท" };
        e.Column.Header = headers.GetValueOrDefault(e.PropertyName, e.PropertyName);
        e.Column.MinWidth = 90; e.Column.Width = e.PropertyName is "TITLE" or "LYR_TITLE" ? 220 : e.PropertyName == "ARTIST" ? 160 : 110;
        if (e.Column is DataGridBoundColumn column)
            column.Binding = new Binding(e.PropertyName) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus };
    }
    void UnlimitedChanged(object sender, RoutedEventArgs e) { if (PerFolder != null) PerFolder.IsEnabled = UnlimitedFolder.IsChecked != true; }
    async Task<ExportPlan?> Prepare()
    {
        if (Kept == null || Kept.Rows.Count == 0 || mustReload) return null;
        SongsGrid.CommitEdit(); SongsGrid.CommitEdit();
        if (ExtremePath.Text.Length == 0 || EmkPath.Text.Length == 0 || NcnPath.Text.Length == 0) throw new InvalidOperationException("เลือกโฟลเดอร์ Extreme, EMK และ NCN ให้ครบก่อน");
        int? count = null;
        if (UnlimitedFolder.IsChecked != true)
        {
            if (!int.TryParse(PerFolder.Text, out var parsed)) throw new InvalidOperationException("กรอกจำนวนเพลงต่อโฟลเดอร์เป็นตัวเลข 1–99,999 หรือเลือกไม่จำกัด");
            count = parsed;
        }
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
            var summary = await Task.Run(() => OperationSummary.Create(plan, all?.Rows.Count ?? 0, Kept?.Rows.Count ?? 0, selection?.Duplicates.Rows.Count ?? 0));
            ShowSummary("ตรวจสรุปก่อนส่งออก", "ยังไม่มีการเปลี่ยนแปลงไฟล์ • ปุ่มสำเนาและย้ายจะให้ตรวจสรุปนี้อีกครั้งก่อนเริ่ม", plan, summary, false);
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
        Start("กำลังตรวจไฟล์ก่อนส่งออก…"); Progress.IsIndeterminate = true;
        try
        {
            var plan = await Prepare(); if (plan == null) return;
            if (plan.Ready == 0) throw new InvalidOperationException("ไม่พบชุดไฟล์ที่พร้อมส่งออก • กดตรวจไฟล์ก่อนส่งออกเพื่อดูรายละเอียด");
            if (plan.Missing > 0 && Dialog($"มีชุดไฟล์ไม่ครบ {plan.Missing:N0} เพลง จะข้ามรายการเหล่านี้\nส่งออกเฉพาะ {plan.Ready:N0} เพลงที่ครบหรือไม่?", "พบไฟล์ขาด", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { StatusText.Text = "ยกเลิกส่งออกแล้ว"; return; }
            var summary = await Task.Run(() => OperationSummary.Create(plan, all?.Rows.Count ?? 0, Kept?.Rows.Count ?? 0, selection?.Duplicates.Rows.Count ?? 0));
            bool enough = move || !summary.FreeBytes.HasValue || summary.FreeBytes.Value >= summary.Bytes;
            if (!ShowSummary(move ? "ย้ายเพลงที่เก็บ" : "สำเนาเพลงที่เก็บ", enough ? "ตรวจจำนวนและปลายทางก่อนเริ่ม • เพลงซ้ำที่คัดออกยังอยู่ต้นทาง" : "พื้นที่ปลายทางไม่พอสำหรับการสำเนา", plan, summary, enough)) { StatusText.Text = "กลับไปตรวจสอบแล้ว • ยังไม่ได้เปลี่ยนไฟล์"; return; }
        if (move)
        {
            if (Dialog($"จะย้ายเพลงที่เก็บไว้ {Kept.Rows.Count:N0} เพลงไปยัง Export และเปลี่ยนรหัสใหม่\nไฟล์เหล่านี้จะออกจากต้นทาง ส่วนเพลงซ้ำที่คัดออกยังอยู่ต้นทาง\n\n{Folders.ExportRoot}\n\nต้องการดำเนินการหรือไม่?", "ยืนยันการย้ายเพลง", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Dialog("ยืนยันอีกครั้ง: รหัสเพลงจะเปลี่ยน และต้องจัดทำฐานข้อมูลใน Extreme ใหม่หลังย้าย\nดำเนินการต่อหรือไม่?", "ยืนยันการเปลี่ยนรหัส", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
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
        FoldersPanel.IsEnabled = FilterPanel.IsEnabled = OptionsPanel.IsEnabled = SongsGrid.IsEnabled = SessionPanel.IsEnabled = false;
        StopButton.IsEnabled = true; UpdateButtons();
    }
    void End()
    {
        busy = false; job?.Dispose(); job = null; Progress.IsIndeterminate = false;
        FoldersPanel.IsEnabled = FilterPanel.IsEnabled = OptionsPanel.IsEnabled = SongsGrid.IsEnabled = SessionPanel.IsEnabled = true;
        StopButton.IsEnabled = false; UpdateButtons();
        if (mustReload) { SongsGrid.IsReadOnly = true; StatusText.Text += " • เปิดฐานข้อมูลใหม่ก่อนทำงานต่อ"; }
    }
    void UpdateButtons()
    {
        CopyButton.IsEnabled = MoveButton.IsEnabled = !busy && !mustReload && Kept?.Rows.Count > 0;
        GroupsButton.IsEnabled = !busy && !mustReload && selection?.GroupCount > 0;
        ParkButton.IsEnabled = !busy && !mustReload && selection?.Duplicates.Rows.Count > 0;
        SaveWorkButton.IsEnabled = !busy && all != null;
        RestoreButton.IsEnabled = !busy && ExtremePath.Text.Length > 0 && EmkPath.Text.Length > 0 && NcnPath.Text.Length > 0;
    }
    async void GroupsClick(object sender, RoutedEventArgs e)
    {
        if (busy || selection == null || rule == null || mustReload) return;
        var window = new GroupsWindow(selection, rule.Value, choices) { Owner = this };
        bool accepted = GroupsOverride?.Invoke(window) ?? window.ShowDialog() == true;
        if (accepted) { choices = window.Choices; await ApplyRule(rule.Value); }
    }
    internal async Task SaveWorkFile(string path)
    {
        if (busy || all == null) return;
        if (!int.TryParse(PerFolder.Text, out int count) || count is < 1 or > 99999) count = 20000;
        var info = new WorkspaceInfo(1, Folders, DatabasePath.Text, databaseHash, rule, EmkFirst.IsChecked == true, StartLetter.SelectedItem?.ToString()?[0] ?? '0', count, UnlimitedFolder.IsChecked == true, new(choices), mustReload, lastHold);
        var songs = all;
        Start("กำลังบันทึกไฟล์งาน…");
        try { await Task.Run(() => Workspace.Save(path, info, songs)); StatusText.Text = "บันทึกไฟล์งานแล้ว: " + path; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    async void SaveWorkClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "บันทึกงานเพื่อเปิดต่อ", Filter = "Mixer Studio work (*.mixwork)|*.mixwork", FileName = "คัดเพลง.mixwork", DefaultExt = ".mixwork" };
        if (dialog.ShowDialog(this) == true) await SaveWorkFile(dialog.FileName);
    }
    internal async Task OpenWorkFile(string path)
    {
        if (busy) return; Start("กำลังเปิดไฟล์งาน…"); Progress.IsIndeterminate = true;
        try
        {
            var opened = await Task.Run(() =>
            {
                var data = Workspace.Load(path); var info = data.Info;
                var result = info.Rule.HasValue ? SongSelection.Filter(data.Songs, info.Rule.Value, info.EmkFirst, job!.Token, info.Choices) : null;
                bool changed = info.DatabaseHash != null && File.Exists(info.Database) && Workspace.HashFile(info.Database) != info.DatabaseHash;
                return (Data: data, Result: result, Changed: changed);
            });
            job!.Token.ThrowIfCancellationRequested();
            var info = opened.Data.Info; loaded = false;
            all = opened.Data.Songs; choices = new(info.Choices); selection = opened.Result; rule = info.Rule;
            mustReload = info.MustReload || opened.Changed; databaseHash = info.DatabaseHash; lastHold = info.LastHold;
            SetFolders(info.Folders); DatabasePath.Text = info.Database; EmkFirst.IsChecked = info.EmkFirst;
            StartLetter.SelectedItem = info.Start.ToString(); PerFolder.Text = info.PerFolder.ToString(); UnlimitedFolder.IsChecked = info.Unlimited;
            Search.Text = ""; ViewMode.SelectedIndex = 0; loaded = true; RefreshView();
            StatusText.Text = opened.Changed ? "DBF ต้นทางเปลี่ยนแล้ว • เปิดฐานข้อมูลใหม่ก่อนจัดไฟล์" : "เปิดไฟล์งานแล้ว • คืนเกณฑ์และตัวเลือกของแต่ละกลุ่มแล้ว";
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดเปิดไฟล์งานแล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { loaded = true; End(); }
    }
    async void OpenWorkClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "เปิดงานที่บันทึกไว้", Filter = "Mixer Studio work (*.mixwork)|*.mixwork" };
        if (dialog.ShowDialog(this) == true) await OpenWorkFile(dialog.FileName);
    }
    async void ParkClick(object sender, RoutedEventArgs e) => await ParkDuplicates();
    internal async Task ParkDuplicates()
    {
        if (busy || mustReload || selection == null || selection.Duplicates.Rows.Count == 0) return;
        if (ExtremePath.Text.Length == 0 || EmkPath.Text.Length == 0 || NcnPath.Text.Length == 0) { Error(new InvalidOperationException("เลือกโฟลเดอร์ให้ครบก่อน")); return; }
        var folders = Folders; var data = selection; Start("กำลังตรวจชุดเพลงซ้ำก่อนพัก…"); Progress.IsIndeterminate = true;
        try
        {
            var plan = await Task.Run(() => Hold.Plan(data.Duplicates, data.Kept, folders, job!.Token));
            var summary = await Task.Run(() => OperationSummary.Create(plan, all?.Rows.Count ?? 0, data.Kept.Rows.Count, data.Duplicates.Rows.Count));
            if (!ShowSummary("พักเพลงซ้ำ", "ย้ายเฉพาะเพลงซ้ำที่คัดออก พร้อมเก็บรหัสและพาธเดิมสำหรับกู้คืน • ชุดที่ใช้ไฟล์ร่วมกับเพลงเก็บจะถูกข้าม", plan, summary, true)) { StatusText.Text = "ยังไม่ได้พักไฟล์"; return; }
            mustReload = true; lastHold = Path.Combine(plan.ExportRoot, Hold.ManifestName); Progress.IsIndeterminate = false;
            var progress = new Progress<ExportProgress>(p => { Progress.Value = p.Done * 100.0 / p.Total; StatusText.Text = $"พักเพลงซ้ำ {p.Done:N0}/{p.Total:N0} • {p.Code}"; });
            var result = await Task.Run(() => Hold.ExecuteAsync(plan, folders, progress, job!.Token));
            StatusText.Text = $"{(result.Cancelled ? "หยุดแล้ว" : "พักแล้ว")} {result.Moved:N0} ไฟล์ • ข้าม {result.Skipped:N0} / ผิดพลาด {result.Failed:N0} เพลง";
            Dialog(StatusText.Text + "\n\nรายการกู้คืน:\n" + result.Manifest + "\n\nกดกู้คืนเพลงเพื่อย้ายกลับได้ จากนั้นจัดทำฐานข้อมูล Extreme ใหม่", "ผลการพักเพลงซ้ำ");
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดตรวจไฟล์แล้ว"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    async void RestoreClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "เลือกรายการกู้คืนเพลง", Filter = "Mixer hold (mixer-hold.json)|mixer-hold.json" };
        if (lastHold != null && File.Exists(lastHold)) { dialog.InitialDirectory = Path.GetDirectoryName(lastHold); dialog.FileName = Path.GetFileName(lastHold); }
        if (dialog.ShowDialog(this) == true) await RestoreHoldFile(dialog.FileName);
    }
    internal async Task RestoreHoldFile(string path)
    {
        if (busy) return;
        if (Dialog("จะกู้คืนเพลงไปยังพาธเดิมในโฟลเดอร์ EMK / NCN ที่เลือก\nหากต้นทางมีไฟล์อยู่แล้วจะไม่เขียนทับ\n\nรายการ: " + path, "ยืนยันกู้คืนเพลง", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var folders = Folders; Start("กำลังกู้คืนเพลง…"); Progress.IsIndeterminate = true; mustReload = true;
        try
        {
            var result = await Task.Run(() => Hold.RestoreAsync(path, folders, job!.Token));
            StatusText.Text = $"กู้คืน {result.Restored:N0} • คืนแล้ว {result.AlreadyRestored:N0} • ผิดพลาด {result.Failed:N0} ไฟล์";
            Dialog(StatusText.Text + "\n\n" + string.Join("\n", result.Errors.Take(10)) + "\nจัดทำฐานข้อมูล Extreme ใหม่ก่อนทำงานต่อ", "ผลการกู้คืนเพลง");
        }
        catch (OperationCanceledException) { StatusText.Text = "หยุดกู้คืนแล้ว • เปิดรายการเดิมเพื่อทำต่อได้"; }
        catch (Exception ex) { Error(ex); }
        finally { End(); }
    }
    void StopClick(object sender, RoutedEventArgs e) { job?.Cancel(); StopButton.IsEnabled = false; StatusText.Text = "กำลังหยุด • จะทำชุดไฟล์เพลงปัจจุบันให้เสร็จก่อน"; }
    void Error(Exception ex) { StatusText.Text = "ทำงานไม่สำเร็จ"; Dialog(ex.Message, "ทำงานไม่สำเร็จ", MessageBoxButton.OK, MessageBoxImage.Warning); }
    void OnClosing(object? sender, CancelEventArgs e) { if (busy) { e.Cancel = true; job?.Cancel(); StatusText.Text = "กำลังหยุดงาน • เมื่อหยุดแล้วสามารถปิดหน้าต่างได้"; } }
}
