using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Composition;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _TtyChildHistoryScene(string provider, bool reading)
    {
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true, SidebarWidth = 260 };
        var owner = new TtyViewModel { Title = $"{provider} TTY · parser review", ActiveProfileLabel = "personal", WorkingPath = "C:/Projects/AI-Cockpit" };
        owner.UpdateChildAgents([
            new BackgroundTask("tty-running", BackgroundTaskKind.SubAgent, "Review the parser change")
            {
                AgentId = "tty-running", SourceRevision = "start-1", AgentType = provider == "Claude" ? "general-purpose" : "default",
                StartedAtUnixMilliseconds = DateTimeOffset.UtcNow.AddSeconds(-72).ToUnixTimeMilliseconds(),
            },
            new BackgroundTask("tty-finished", BackgroundTaskKind.SubAgent, "Draft the release notes")
            {
                AgentId = "tty-finished", SourceRevision = "start-2", State = "completed", AgentType = "Explore", DurationMilliseconds = 160000,
            },
            new BackgroundTask("tty-unknown", BackgroundTaskKind.SubAgent, "Recovered child without live hooks")
            {
                AgentId = "tty-unknown", SourceRevision = "file-1", State = "unknown",
            },
        ]);
        cockpit.Sessions.Clear();
        cockpit.Sessions.Add(owner);
        cockpit.SelectSessionCommand.Execute(owner);
        if (reading)
        {
            owner.ChildAgents[1].OpenCommand.Execute(null);
            if (owner.ChildTranscript is { } transcript)
            {
                transcript.Status = "";
                transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "The review is complete. These entries belong to the selected TTY child; the parent terminal continues running."));
            }
        }
        return new MainWindow { DataContext = cockpit };
    }

    // AC-2009: the real sidebar — one owner with running, finished and unknown children, one without any.
    private static Window _ChildHistorySidebar()
    {
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true, SidebarWidth = 260 };
        SessionViewModel[] owners = cockpit.Sessions.OfType<SessionViewModel>().Take(2).ToArray();
        _ApplyDemoChildren(owners[0]);
        cockpit.SelectSessionCommand.Execute(owners[0]);
        if (owners[1].HasChildAgents || !owners[0].AreSidebarChildrenExpanded)
        {
            throw new InvalidOperationException("The sidebar scene needs one expanded owner and one owner without children.");
        }
        return new MainWindow { DataContext = cockpit };
    }

    // Clicks through the sidebar itself: "+ N finished", then the finished child's row.
    private static void _OpenSidebarChild(Window window)
    {
        Button more = window.GetVisualDescendants().OfType<Button>().First(button =>
            button.IsEffectivelyVisible
            && button.DataContext is SessionPanelViewModel session && ReferenceEquals(button.Command, session.ShowFinishedChildAgentsCommand));
        more.Command?.Execute(more.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Button row = window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible
            && button.DataContext is ChildAgentViewModel { Reference.AgentId: "same-id" } && ToolTip.GetTip(button) is "Open this sub-agent's read-only transcript");
        var child = row.DataContext as ChildAgentViewModel
            ?? throw new InvalidOperationException("The sidebar row has no provider child context.");
        row.Command?.Execute(row.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        if (child.Owner.ChildTranscript is not { } transcript || !ReferenceEquals(transcript.Child, child) || !child.Owner.IsSelected)
        {
            throw new InvalidOperationException("The sidebar child row must select its owner and open that child.");
        }
        transcript.Status = "";
        transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "Draft the release notes for this change."));
        transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "The release notes are drafted. These entries belong to the selected child only."));
    }

    private static void _ApplyDemoChildren(SessionViewModel owner)
    {
        var running = new BackgroundTask("running-id", BackgroundTaskKind.SubAgent, "Review the parser change")
        {
            AgentId = "running-id", RunId = owner.PaneId, LastTool = "Grep", Tokens = 18400,
            AgentType = "general-purpose", DurationMilliseconds = 72000,
        };
        owner.Apply(new BackgroundTasksChanged
        {
            SessionId = owner.PaneId,
            Tasks = [running],
            Children = [running, new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Draft the release notes")
            {
                AgentId = "same-id", RunId = owner.PaneId, State = "completed", LastTool = "Write", Tokens = 31200,
                AgentType = "general-purpose", DurationMilliseconds = 160000,
            }, new BackgroundTask("unknown-id", BackgroundTaskKind.SubAgent, "Check the CI log")
            {
                AgentId = "unknown-id", RunId = owner.PaneId, State = "unknown", AgentType = "Explore",
            }],
        });
    }

    private static Window _ChildHistoryPopout() => _ChildHistoryScene("popout");
    private static Window _ChildHistoryScene(string state)
    {
        var cockpit = new CockpitViewModel { GlobalSingleSessionLayout = true, SidebarWidth = 260 };
        SessionViewModel[] owners = cockpit.Sessions.OfType<SessionViewModel>().Take(2).ToArray();
        foreach (var owner in owners)
        {
            _ApplyDemoChildren(owner);
        }
        if (owners.FirstOrDefault() is { } selected && state != "popout")
        {
            selected.ChildAgents[state == "running" ? 0 : 1].OpenCommand.Execute(null);
            if (selected.ChildTranscript is { } transcript)
            {
                transcript.Status = state == "unavailable" ? "Not available: owner connection has stopped" : state == "empty" ? "No entries" : "";
                if (state is "finished" or "running")
                {
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "Review the session driver and report your findings."));
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.ToolResult, "Read · src/Sessions/Driver.cs\nThe driver routes child events by owner and run."));
                    transcript.Rows.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, state == "running" ? "The review is in progress. Child events remain isolated from the parent transcript." : "The review is complete. Child events remain isolated from the parent transcript."));
                    if (transcript.ParentPrompt != "Review the session driver and report your findings." || transcript.TranscriptRows.Count() != 2)
                    {
                        throw new InvalidOperationException("The parent prompt must appear once in its own card, before the child entries.");
                    }
                    transcript.Rows.RemoveAt(0);
                    if (transcript.ParentPrompt != "Review the session driver and report your findings." || transcript.TranscriptRows.Count() != 2)
                    {
                        throw new InvalidOperationException("Sliding the transcript window must preserve the original parent prompt.");
                    }
                }
            }
        }
        return new MainWindow { DataContext = cockpit };
    }
}
