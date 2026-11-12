using System.Windows;
using Mixer.Core;
namespace MixerStudio;
public partial class SummaryWindow : Window
{
    public SummaryWindow(string action, string note, OperationSummary summary, ExportPlan plan, bool canRun)
    {
        InitializeComponent(); ActionTitle.Text = action; ActionNote.Text = note;
        CountsText.Text = $"ข้อมูล {summary.Loaded:N0} เพลง  •  เก็บ {summary.Kept:N0}  •  ซ้ำ {summary.Duplicates:N0}\nพร้อมทำงาน {summary.ReadySongs:N0} เพลง / {summary.Files:N0} ไฟล์  •  ข้าม {summary.BlockedSongs:N0} เพลง\nขนาดไฟล์ {OperationSummary.Size(summary.Bytes)}  •  พื้นที่ว่าง {(summary.FreeBytes.HasValue ? OperationSummary.Size(summary.FreeBytes.Value) : "ตรวจไม่ได้")}";
        LocationText.Text = "ปลายทาง: " + summary.Destination;
        Details.Text = "ตัวอย่างรายการ:\n" + string.Join("\n", plan.Songs.Where(s => s.Problems.Count == 0).Take(12).Select(s => $"{s.Kind} {s.OldCode} → {s.NewCode}")) + "\n\nรายการที่จะข้าม:\n" + (summary.Problems.Length == 0 ? "ไม่มี" : string.Join("\n", summary.Problems.Take(40)));
        ConfirmButton.IsEnabled = canRun && summary.ReadySongs > 0;
        if (!canRun) ConfirmButton.Visibility = Visibility.Collapsed;
    }
    void ConfirmClicked(object sender, RoutedEventArgs e) => DialogResult = true;
    void CancelClicked(object sender, RoutedEventArgs e) => DialogResult = false;
}
