using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The one MCP checklist behind the profile editor, the New-session dialog and the project editor (AC-140). Three
/// copies of the same rows is how the project editor came to offer servers the other two had stopped showing, so
/// what matters here is that all three use this control and that its collapsed header keeps telling the truth.
/// </summary>
[Collection("avalonia")]
public class McpServerChecklistTests
{
    // What a collapsed header says about the list under it: naming one switched-off server makes it findable
    // without expanding, past a handful the count reads better, and an empty list adds nothing to its own name.
    // Order-independent here — `_RefreshSummary` joins the off names in list order, and no row has two of them.
    [Theory]
    [InlineData(new[] { "depot", "playwright" }, new[] { "youtrack" }, "MCP servers · youtrack off")]
    [InlineData(new[] { "depot" }, new[] { "youtrack", "playwright", "docker", "k8s" }, "MCP servers · 1 of 5 selected")]
    [InlineData(new string[0], new string[0], "MCP servers")]
    public void TheCollapsedHeader_SaysWhatTheListHolds(string[] on, string[] off, string expected) =>
        HeadlessAvalonia.Run(() => Assert.Equal(expected, _Checklist(on, off).SummaryText));

    private static McpServerChecklist _Checklist(string[] on, string[] off)
    {
        var servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel>();
        foreach (var name in on)
        {
            servers.Add(new McpServerSelectionItemViewModel(name));
        }

        foreach (var name in off)
        {
            servers.Add(new McpServerSelectionItemViewModel(name) { IsEnabledForSession = false });
        }

        return new McpServerChecklist { Servers = servers };
    }

    [Fact]
    public void TickingABox_MovesTheSummary_SoACollapsedListStillSaysWhatItHolds() => HeadlessAvalonia.Run(() =>
    {
        var youtrack = new McpServerSelectionItemViewModel("youtrack") { IsEnabledForSession = false };
        var checklist = new McpServerChecklist
        {
            Servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel>
            {
                new("depot"),
                youtrack,
            },
        };

        youtrack.IsEnabledForSession = true;

        Assert.Equal("MCP servers · all 2 selected", checklist.SummaryText);
    });

    [Fact]
    public void RowsRebuilt_AreSummarisedToo() => HeadlessAvalonia.Run(() =>
    {
        // The New-session dialog rebuilds its rows on every project switch; a count that only ever listened to the
        // first set would then freeze at whatever the previous project had.
        var servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel> { new("depot") };
        var checklist = new McpServerChecklist { Servers = servers };

        servers.Clear();
        servers.Add(new McpServerSelectionItemViewModel("depot"));
        servers.Add(new McpServerSelectionItemViewModel("youtrack") { IsEnabledForSession = false });
        servers[0].IsEnabledForSession = false;

        Assert.Equal("MCP servers · depot, youtrack off", checklist.SummaryText);
    });

    [Fact]
    public void TheProjectEditor_UsesTheSharedChecklist() => HeadlessAvalonia.Run(() =>
    {
        var window = new ProjectDialog { DataContext = new ProjectDialogViewModel() };
        window.Show();
        window.UpdateLayout();

        var checklists = window.GetVisualDescendants().OfType<McpServerChecklist>().ToList();
        window.Close();

        Assert.Single(checklists);
    });
}
