using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static void _SeedAgentsOverviewChildren(CockpitViewModel cockpit)
    {
        var first = (SessionViewModel)cockpit.Sessions[0];
        first.Apply(new TurnCompleted
        {
            SessionId = first.PaneId, Subtype = "success", Result = "", IsError = false,
            Usage = new TokenUsage(100, 0, 0, 0),
        });
        first.UpdateChildAgents([
            new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Draft the release notes")
            {
                AgentId = "same-id", RunId = "atlas-run", State = "completed", AgentType = "general-purpose",
                LastTool = "Write", Tokens = 40, DurationMilliseconds = 160000,
            },
            new BackgroundTask("unknown-id", BackgroundTaskKind.SubAgent, "Check the CI log")
            {
                AgentId = "unknown-id", RunId = "atlas-run", State = "unknown", AgentType = "Explore",
            },
        ]);
        first.SessionStatus = SessionStatus.Busy;
        var second = cockpit.Sessions[1];
        second.UpdateChildAgents([
            new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Review the parser change")
            {
                AgentId = "same-id", RunId = "harbor-run", AgentType = "Explore", LastTool = "Grep", DurationMilliseconds = 72000,
            },
        ]);
        second.SessionStatus = SessionStatus.Done;
        cockpit.DelegatedTasks.Tasks.RemoveAt(1);
    }

    private static void _OpenAgentChild(Window window)
    {
        var overview = window.GetVisualDescendants().OfType<AgentsOverviewView>().Single();
        var button = overview.GetVisualDescendants().OfType<Button>().First(button =>
            button.DataContext is ChildAgentViewModel child && child.Owner.Title == "atlas · parser cleanup"
            && child.Reference.AgentId == "same-id" && button.Command is not null);
        var selected = button.DataContext as ChildAgentViewModel
            ?? throw new InvalidOperationException("The child action has no provider child context.");
        button.Command?.Execute(button.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        if (!selected.Owner.IsReadingChild || !ReferenceEquals(selected.Owner.ChildTranscript?.Child, selected))
        {
            throw new InvalidOperationException("The overview child action must open that owner's read-only child.");
        }
    }
}
