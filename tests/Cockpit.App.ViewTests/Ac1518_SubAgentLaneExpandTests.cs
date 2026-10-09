#if DEBUG
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

// AC-1518, layer 2 (an algorithm): opening a sub-agent anchor built a row view for every nested row in one pass on the
// UI thread, measured at 5,7 s for 2000 rows. Opening a lane of any length builds one batch per pump window instead.
[Collection("avalonia")]
public sealed class Ac1518_SubAgentLaneExpandTests
{
    [Fact]
    public Task OpeningALongLane_BuildsOneBatchAtATime_UntilEveryRowIsShown() => HeadlessAvalonia.RunAsync(async () =>
    {
        var anchor = new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Agent({})") { ToolName = "Agent", ToolUseId = "agent" };
        for (var step = 0; step < 400; step++)
        {
            anchor.SubAgentRows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolUse, "Tool: Read({})") { ToolName = "Read", ToolUseId = $"r{step}" });
        }

        var vm = new SessionViewModel();
        vm.Transcript.Add(anchor);
        var view = new SessionView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 760 };
        window.Show();
        window.UpdateLayout();

        anchor.IsSubAgentExpanded = true;
        window.UpdateLayout();
        var lane = view.GetVisualDescendants().OfType<ItemsControl>().Single(list => list.Items.Contains(anchor.SubAgentRows[0]));
        Assert.Equal(TranscriptEntryViewModel.SubAgentRowsPerWindow, lane.GetRealizedContainers().Count());

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (anchor.ShownSubAgentRows.Count < 400 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(anchor.SubAgentRows, anchor.ShownSubAgentRows);
        anchor.IsSubAgentExpanded = false;
        Assert.Empty(anchor.ShownSubAgentRows);
        window.Close();
    });
}
#endif
