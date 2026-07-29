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
    [Fact]
    public void TheHeader_CountsWhatIsTicked() => HeadlessAvalonia.Run(() =>
    {
        var checklist = new McpServerChecklist
        {
            Servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel>
            {
                new("depot"),
                new("youtrack") { IsEnabledForSession = false },
                new("playwright"),
            },
        };

        Assert.Equal("MCP servers · 2 of 3 selected", checklist.SummaryText);
    });

    [Fact]
    public void TickingABox_MovesTheCount_SoACollapsedListStillSaysWhatItHolds() => HeadlessAvalonia.Run(() =>
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

        Assert.Equal("MCP servers · 2 of 2 selected", checklist.SummaryText);
    });

    [Fact]
    public void RowsRebuilt_AreCountedToo() => HeadlessAvalonia.Run(() =>
    {
        // The New-session dialog rebuilds its rows on every project switch; a count that only ever listened to the
        // first set would then freeze at whatever the previous project had.
        var servers = new System.Collections.ObjectModel.ObservableCollection<McpServerSelectionItemViewModel> { new("depot") };
        var checklist = new McpServerChecklist { Servers = servers };

        servers.Clear();
        servers.Add(new McpServerSelectionItemViewModel("depot"));
        servers.Add(new McpServerSelectionItemViewModel("youtrack") { IsEnabledForSession = false });
        servers[0].IsEnabledForSession = false;

        Assert.Equal("MCP servers · 0 of 2 selected", checklist.SummaryText);
    });

    [Fact]
    public void WithNoServers_TheHeaderIsJustTheName() => HeadlessAvalonia.Run(() =>
    {
        Assert.Equal("MCP servers", new McpServerChecklist().SummaryText);
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
