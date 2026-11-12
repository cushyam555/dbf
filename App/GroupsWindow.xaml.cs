using System.Data;
using System.Windows;
using System.Windows.Controls;
using Mixer.Core;
namespace MixerStudio;
public partial class GroupsWindow : Window
{
    public record Group(int Number, string Label, string Key, DataTable Table);
    readonly List<Group> groups;
    readonly DuplicateRule rule;
    public Dictionary<string, int> Choices { get; }
    public GroupsWindow(SelectionResult result, DuplicateRule rule, IReadOnlyDictionary<string, int> choices)
    {
        InitializeComponent(); this.rule = rule; Choices = new(choices);
        groups = result.Review.AsEnumerable().GroupBy(r => (int)r["กลุ่มซ้ำ"]).Where(g => g.Count() > 1).Select(g =>
        {
            var first = g.First(); string name = first[rule == DuplicateRule.TitleAndLyric ? "TITLE" : "ARTIST"].ToString()!.Trim();
            return new Group(g.Key, $"{g.Key}. {name} ({g.Count()} เพลง)", SongSelection.GroupKey(first, rule), g.CopyToDataTable());
        }).ToList();
        GroupList.ItemsSource = groups; if (groups.Count > 0) GroupList.SelectedIndex = 0; Counts();
    }
    void SearchGroups(object sender, TextChangedEventArgs e)
    {
        if (groups == null) return;
        var text = GroupSearch.Text.Trim(); GroupList.ItemsSource = groups.Where(g => g.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase)).ToList();
        GroupList.SelectedIndex = 0;
    }
    void GroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Candidates == null) return;
        if (GroupList.SelectedItem is Group group)
        {
            Candidates.ItemsSource = group.Table.DefaultView; Candidates.SelectedIndex = 0;
            Reason.Text = group.Table.Rows[0]["เหตุผล"].ToString() + "\nLYR_TITLE: " + group.Table.Rows[0]["LYR_TITLE"].ToString()!.Trim();
        }
        else { Candidates.ItemsSource = null; Reason.Text = "ไม่พบกลุ่มตรงกับคำค้นหา"; }
    }
    internal void PickCandidate(int id)
    {
        var group = groups.FirstOrDefault(g => g.Table.AsEnumerable().Any(r => (int)r[SongSelection.RowId] == id)) ?? throw new InvalidOperationException("ไม่พบรายการที่เลือก");
        var winner = group.Table.AsEnumerable().Single(r => (int)r[SongSelection.RowId] == id);
        Choices[group.Key] = id;
        foreach (DataRow row in group.Table.Rows) { row["ผลการคัด"] = (int)row[SongSelection.RowId] == id ? "เก็บ" : "ซ้ำ"; row["เก็บรหัส"] = winner["CODE"].ToString()!.Trim(); }
        Counts();
    }
    void KeepClicked(object sender, RoutedEventArgs e) { if (Candidates.SelectedItem is DataRowView row) PickCandidate((int)row[SongSelection.RowId]); }
    void Counts() => ChoiceCount.Text = $"กลุ่มเพลงซ้ำ {groups.Count:N0} กลุ่ม • เลือกเก็บเอง {groups.Count(g => Choices.ContainsKey(g.Key)):N0} กลุ่ม";
    void AcceptClicked(object sender, RoutedEventArgs e) { DialogResult = true; }
    void CancelClicked(object sender, RoutedEventArgs e) { DialogResult = false; }
}
