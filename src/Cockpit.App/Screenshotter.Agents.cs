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
        SessionPanelViewModel second = cockpit.Sessions[1];
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
        AgentsOverviewView overview = window.GetVisualDescendants().OfType<AgentsOverviewView>().Single();
        Button button = overview.GetVisualDescendants().OfType<Button>().First(button =>
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

    // AC-1932: the Agents panel with a swarm member's sessions among the local ones, awake or asleep.
    private static MainWindow _AgentsSwarm(int width, int height, bool asleep) =>
        new() { DataContext = _AgentsSwarmCockpit(asleep), Width = width, Height = height };

    // AC-1933: the same board as an overlay, with the rail icon switched on.
    private static Window _AgentsPalette(bool filtered)
    {
        var cockpit = _AgentsSwarmCockpit(asleep: false);
        cockpit.AgentsPaletteRailIcon = true;
        cockpit.AgentsOverview.ShowDelegatedTasks = true;
        cockpit.AgentsOverview.ShowOnlyWaiting = filtered;
        if (cockpit.AgentsPaletteShortcut?.Gesture != "Ctrl+Shift+A")
        {
            throw new InvalidOperationException("The palette must default to Ctrl+Shift+A.");
        }
        var palette = new AgentsPaletteViewModel(cockpit.AgentsOverview);
        palette.MoveColumn(1);
        return new AgentsPaletteDialog { DataContext = palette };
    }

    // AC-1933: where the palette is reached from — the rail icon switched on — and its Options page.
    private static MainWindow _AgentsPaletteEntries(int width, int height)
    {
        var cockpit = _AgentsSwarmCockpit(asleep: false);
        cockpit.AgentsPaletteRailIcon = true;
        return new MainWindow { DataContext = cockpit, Width = width, Height = height };
    }

    private static OptionsDialog _AgentsPaletteOptions()
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel(), Width = 1000, Height = 520 };
        dialog.SelectCategory("agents-overview");
        return dialog;
    }

    private static CockpitViewModel _AgentsSwarmCockpit(bool asleep)
    {
        var panels = new Docking.DockPanelRegistry();
        var cockpit = new CockpitViewModel(dockPanelRegistry: panels)
        {
            OpenDockPanelId = AgentsOverviewViewModel.DockPanelId, DockRailWidth = 520, IsZoomed = true,
        };
        cockpit.Projects.Projects.Add(new Cockpit.Core.Projects.Project("cockpit", "cockpit"));
        cockpit.Sessions[0].Title = "atlas · parser cleanup";
        cockpit.Sessions[0].ActiveProfileLabel = "Coder";
        cockpit.Sessions[0].ProjectId = "cockpit";
        cockpit.Sessions[0].SessionStatus = SessionStatus.Busy;
        cockpit.Sessions[1].Title = "harbor · docs pass";
        cockpit.Sessions[1].ActiveProfileLabel = "Writer";
        cockpit.Sessions[1].SessionStatus = SessionStatus.Done;
        var options = new SidebarSwarmOptions(false);
        var nodes = new _SceneNodeSessions
        {
            ReadSnapshot = name => new(name,
                [new("desktop-review", "AC-1805 notification center", "Coder", "Awaiting your review", "NeedsAttention", true),
                    new("desktop-build", "AC-1810 build fix", "Coder", "Running the tests", "Busy")],
                [], []),
        };
        cockpit.ShowSwarm(options, new SidebarLeadership(options.Laptop), nodes, poll: false);
        cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
        _ = cockpit.AgentsOverview;
        if (asleep)
        {
            options.Asleep = true;
            cockpit.RefreshSwarmGroupsAsync().GetAwaiter().GetResult();
        }
        var cards = cockpit.AgentsOverview.Columns.SelectMany(column => column.Cards).Where(card => card.Machine == "Desktop").ToList();
        if (cards.Count != 2 || cards.Any(card => card.IsAsleep != asleep)
            || (asleep && !cards.All(card => card.LastSeenLabel.StartsWith("Last seen", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException("Desktop's sessions must stand in the overview, greyed with Last seen once it sleeps.");
        }
        if (!asleep && cockpit.AgentsOverview.Columns[1].Cards.Count(card => card.Machine == "Desktop") != 1)
        {
            throw new InvalidOperationException("Desktop's waiting session must stand in Waiting for you.");
        }
        return cockpit;
    }
}
