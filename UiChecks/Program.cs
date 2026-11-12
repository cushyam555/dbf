using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mixer.Core;
using MixerStudio;

internal static class Program
{
    static int passed;
    [STAThread] static int Main()
    {
        int result = 1;
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MixerStudio;component/Theme.xaml", UriKind.Relative) });
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timeout.Tick += (_, _) => { Console.Error.WriteLine("FAIL: UI checks timed out"); Environment.Exit(2); }; timeout.Start();
        window.Loaded += async (_, _) =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Mixer-UI-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { await Test(window, root); result = 0; Console.WriteLine($"All {passed} Windows UI checks passed."); }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                timeout.Stop();
                try { Directory.Delete(root, true); } catch (IOException) { }
                app.Shutdown(result);
            }
        };
        app.Run(window); return result;
    }
    static T Control<T>(MainWindow w, string name) where T : class => (T)w.FindName(name);
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL UI: " + name); passed++; Console.WriteLine("PASS UI " + name); }
    static string Codes(MainWindow w) => string.Join(',', ((DataView)Control<DataGrid>(w, "SongsGrid").ItemsSource).Cast<DataRowView>().Select(r => r["CODE"].ToString()!.Trim()));
    static async Task Ready(MainWindow w)
    {
        var timer = Stopwatch.StartNew();
        while (!Control<Button>(w, "CopyButton").IsEnabled)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("UI did not finish: " + Control<TextBlock>(w, "StatusText").Text);
            await Task.Delay(50);
        }
    }
    static void Click(MainWindow w, string name) => Control<Button>(w, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static async Task Test(MainWindow w, string root)
    {
        var folders = SourceFolders.FromExtreme(root); w.SetFolders(folders);
        var dbf = Path.Combine(root, "song.dbf"); WriteDbf(dbf);
        void Put(string path, string data) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, data); }
        Put(Path.Combine(folders.Emk, "A.emk"), "fixture EMK");
        foreach (var code in new[] { "B", "C", "D" })
        foreach (var (dir, ext) in new[] { ("Song", ".mid"), ("Lyrics", ".lyr"), ("Cursor", ".cur") })
            Put(Path.Combine(folders.Ncn, dir, "x", code + ext), "fixture " + code + ext);
        await w.LoadDatabaseFile(dbf);
        Check(new[] { "ExtremePath", "EmkPath", "NcnPath" }.All(n => Grid.GetColumn(Control<TextBox>(w, n)) == 1 && Control<TextBox>(w, n).ActualWidth > 300), "folder paths have their own readable layout column");
        Check(Control<TextBlock>(w, "TotalText").Text == "4", "load actual Song.dbf into screen");
        Check(Control<DataGrid>(w, "SongsGrid").Columns.Count >= 6, "display all database columns");
        Click(w, "FilterTitleButton"); await Ready(w);
        Check(Codes(w) == "A,C,D", "title + lyric button keeps expected songs");
        Check(Control<TextBlock>(w, "DuplicateText").Text == "1", "duplicate counter matches title rule");
        Click(w, "FilterArtistButton"); await Ready(w);
        Check(Codes(w) == "A,D", "artist + lyric button keeps expected songs");
        windowPreview(w);
        Control<CheckBox>(w, "EmkFirst").IsChecked = false; await Ready(w);
        Check(Codes(w) == "B,D", "NCN priority changes retained song");
        Control<ComboBox>(w, "ViewMode").SelectedIndex = 1;
        Check(Codes(w) == "C,A", "duplicate view shows discarded alternatives");
        Control<ComboBox>(w, "ViewMode").SelectedIndex = 2;
        Check(((DataView)Control<DataGrid>(w, "SongsGrid").ItemsSource).Count == 4, "review view shows all decisions");
        Control<ComboBox>(w, "ViewMode").SelectedIndex = 0;
        Control<TextBox>(w, "Search").Text = "%’[]'";
        Check(((DataView)Control<DataGrid>(w, "SongsGrid").ItemsSource).Count == 0, "search escapes special expression characters");
        Control<TextBox>(w, "Search").Text = "B";
        Check(Codes(w) == "B", "search filters visible table");
        // Export must still include all retained rows even while the table search shows only B.
        Control<TextBox>(w, "PerFolder").Text = "1";
        Control<CheckBox>(w, "UnlimitedFolder").IsChecked = true;
        Check(!Control<TextBox>(w, "PerFolder").IsEnabled, "unlimited option disables the numeric folder limit");
        await WithDialogs(w, "CopyButton", new() { ["ผลการส่งออก"] = 1 });
        Check(Directory.GetFiles(Path.Combine(folders.ExportRoot, "NCN", "Export", "Song", "0")).Length == 2, "copy button exports both retained songs despite table search");
        Check(File.Exists(Path.Combine(folders.Ncn, "Song", "x", "B.mid")), "copy button preserves source");
        Check(!Directory.Exists(Path.Combine(folders.ExportRoot, "EMK", "Export")), "copy does not export discarded EMK");
        Check(Control<TextBlock>(w, "ProgressText").Text.Contains("NCN: 2"), "per-type progress reflects completed copy");
        Control<TextBox>(w, "Search").Text = "";
        Directory.Delete(folders.ExportRoot, true);
        await WithDialogs(w, "MoveButton", new() { ["ยืนยันการย้ายเพลง"] = 6, ["ยืนยันการเปลี่ยนรหัส"] = 6, ["ผลการส่งออก"] = 1 });
        Check(!File.Exists(Path.Combine(folders.Ncn, "Song", "x", "B.mid")) && !File.Exists(Path.Combine(folders.Ncn, "Lyrics", "x", "D.lyr")), "move button moves retained NCN sets");
        Check(File.Exists(Path.Combine(folders.Emk, "A.emk")) && File.Exists(Path.Combine(folders.Ncn, "Song", "x", "C.mid")), "move leaves duplicate alternatives at source");
        Check(!Control<Button>(w, "CopyButton").IsEnabled && !Control<Button>(w, "MoveButton").IsEnabled, "stale database disabled after moving files");
    }
    static async Task WithDialogs(MainWindow window, string button, Dictionary<string, int> answers)
    {
        var observed = new HashSet<string>();
        // Native message boxes use the normal production implementation. Only this fixture runner supplies responses.
        window.DialogOverride = (message, title, buttons, image, defaultResult) =>
        {
            if (!answers.TryGetValue(title, out int answer)) throw new InvalidOperationException("Unexpected UI dialog: " + title + " / " + message);
            observed.Add(title);
            if (title.StartsWith("ยืนยัน")) Check(buttons == MessageBoxButton.YesNo && defaultResult == MessageBoxResult.No, "move confirmation defaults to No");
            return answer == 6 ? MessageBoxResult.Yes : MessageBoxResult.OK;
        };
        try
        {
            Click(window, button);
            var timer = Stopwatch.StartNew();
            while (!observed.Contains("ผลการส่งออก") || Control<Button>(window, "StopButton").IsEnabled)
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Button did not complete: " + button + " / " + Control<TextBlock>(window, "StatusText").Text);
                await Task.Delay(50);
            }
            foreach (string title in answers.Keys) Check(observed.Contains(title), button + " dialog: " + title);
        }
        finally { window.DialogOverride = null; }
    }
    static void windowPreview(MainWindow window)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(Environment.CurrentDirectory, "dist"); Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, "MixerStudio-preview.png")); encoder.Save(output);
        Check(output.Length > 0, "save real WPF screen preview");
    }
    static void WriteDbf(string path)
    {
        string[] columns = ["CODE", "TYPE", "SUB_TYPE", "TITLE", "ARTIST", "LYR_TITLE"];
        string[][] rows = [["B", "MIDI", "NCN", "T1", "Artist1", "Lyric1"], ["A", "MIDI", "EMK", "T1", "Artist1", "Lyric1"], ["C", "MIDI", "NCN", "T2", "Artist1", "Lyric1"], ["D", "MIDI", "NCN", "T3", "Artist3", "Lyric3"]];
        using var writer = new BinaryWriter(File.Create(path));
        var header = new byte[32]; header[0] = 3; BitConverter.GetBytes(rows.Length).CopyTo(header, 4); BitConverter.GetBytes((ushort)(32 + 32 * columns.Length + 1)).CopyTo(header, 8); BitConverter.GetBytes((ushort)(1 + 40 * columns.Length)).CopyTo(header, 10); writer.Write(header);
        foreach (var name in columns) { var descriptor = new byte[32]; Encoding.ASCII.GetBytes(name).CopyTo(descriptor, 0); descriptor[11] = (byte)'C'; descriptor[16] = 40; writer.Write(descriptor); } writer.Write((byte)13);
        foreach (var row in rows) { writer.Write((byte)' '); foreach (var cell in row) writer.Write(Encoding.ASCII.GetBytes(cell.PadRight(40))); } writer.Write((byte)26);
    }
}
