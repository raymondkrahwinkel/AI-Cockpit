using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;
using Cockpit.App.Composition;
using Cockpit.Infrastructure.Sessions;

namespace Cockpit.App.ViewTests;

// AC-531: detached work survives a normal turn, but a stopped owner cannot retain a running child.
[Collection("avalonia")]
public class SessionBackgroundTaskPopoutTests
{
    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    private static TurnCompleted Turn() =>
        new() { SessionId = "s1", Subtype = "success", Result = "done", IsError = false };

    // AC-2009, layer 2 (view model): the sidebar expander follows the owner's own children and a row opens
    // exactly that owner's child, also when two owners carry the same agent id.
    [Fact]
    public void SidebarChildren_ExpandOnlyWithChildren_AndOpenTheOwnersOwnChild() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();
        SessionViewModel[] owners = cockpit.Sessions.OfType<SessionViewModel>().Take(2).ToArray();
        static BackgroundTasksChanged Children(SessionViewModel owner) => new()
        {
            SessionId = owner.PaneId,
            Tasks = [],
            Children =
            [
                new BackgroundTask("same-id", BackgroundTaskKind.SubAgent, "Review") { AgentId = "same-id", RunId = owner.PaneId },
                new BackgroundTask("done-id", BackgroundTaskKind.SubAgent, "Notes") { AgentId = "done-id", RunId = owner.PaneId, State = "completed" },
            ],
        };
        cockpit.SelectSessionCommand.Execute(owners[1]);
        owners[0].Apply(Children(owners[0]));
        Assert.False(owners[1].HasChildAgents);
        Assert.False(owners[1].AreSidebarChildrenExpanded);
        Assert.False(owners[0].AreSidebarChildrenExpanded);

        cockpit.SelectSessionCommand.Execute(owners[0]);
        Assert.True(owners[0].AreSidebarChildrenExpanded);
        Assert.Equal("2 sub-agents · 1 running", owners[0].SidebarChildSummary);
        Assert.Equal("+ 1 finished", owners[0].FoldedFinishedChildText);
        Assert.False(owners[0].ChildAgents[1].IsShownInSidebar);
        owners[0].ShowFinishedChildAgentsCommand.Execute(null);
        Assert.True(owners[0].ChildAgents[1].IsShownInSidebar);
        owners[0].AreSidebarChildrenExpanded = false;
        cockpit.SelectSessionCommand.Execute(owners[1]);
        cockpit.SelectSessionCommand.Execute(owners[0]);
        Assert.False(owners[0].AreSidebarChildrenExpanded);

        owners[1].Apply(Children(owners[1]));
        ChildAgentViewModel picked = owners[1].ChildAgents[0];
        cockpit.OpenChildTranscriptCommand.Execute(picked);
        Assert.True(owners[1].IsSelected);
        Assert.Same(picked, owners[1].ChildTranscript?.Child);
        Assert.True(picked.IsOpenInTranscript);
        Assert.Null(owners[0].ChildTranscript);
        Assert.False(owners[0].ChildAgents[0].IsOpenInTranscript);
    });

    // --- The gap this ticket closes (AC-532's review flagged it explicitly) ---

    [Fact]
    public void TurnCompleting_WithASubAgentAndAShellStillRunning_DoesNotClearTheBackgroundList() => HeadlessAvalonia.Run(() =>
    {
        // This is the regression AC-532's own review named: unlike _activeToolCalls, which TurnCompleted clears
        // unconditionally (leaving the composer looking idle), the background-work list must survive the same
        // event — a detached sub-agent or shell keeps running after the turn that spawned it ends.
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        session.Apply(Turn());

        Assert.True(session.HasBackgroundTasks);
        Assert.Equal(2, session.BackgroundTaskCount);
        Assert.Single(session.BackgroundSubAgents);
        Assert.Single(session.BackgroundShells);
        Assert.False(session.HasActiveToolActivity, "sanity check: the tool-activity band this ticket does not own still clears as AC-532 built it");
    });

    [Fact]
    public Task ASessionError_ClearsTheBackgroundList_SoACrashedSessionDoesNotShowPhantomWork() => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;
        var running = new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1");
        var finished = new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Finished review") { State = "completed", Tokens = 42 };
        BackgroundTasksChanged outstanding = Outstanding(running, new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")) with
        {
            Children = [running, finished],
        };
        session.Apply(outstanding);

        session.Apply(new SessionError { SessionId = "s1", Message = "the driver died" });

        Assert.False(session.HasBackgroundTasks);
        Assert.Equal(0, session.BackgroundTaskCount);
        Assert.Empty(session.BackgroundSubAgents);
        Assert.Empty(session.BackgroundShells);
        Assert.Equal("unknown", session.ChildAgents[0].Reference.State);
        Assert.Same(finished, session.ChildAgents[1].Reference);

        var fold = new SessionLiveStateFold();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        fold.Apply(outstanding, default, interruptRequested: false, now);
        fold.Apply(Turn(), default, interruptRequested: false, now);
        Assert.Same(running, fold.Snapshot(isBusy: false).Children[0]);
        fold.Apply(Turn(), default, interruptRequested: true, now);
        Assert.Equal("unknown", fold.Snapshot(isBusy: false).Children[0].State);
        Assert.Same(finished, fold.Snapshot(isBusy: false).Children[1]);

        session.Apply(outstanding);
        await session.DisposeAsync();
        Assert.Equal("unknown", session.ChildAgents[0].Reference.State);
        Assert.Same(finished, session.ChildAgents[1].Reference);

        var tty = new TtyViewModel();
        tty.UpdateChildAgents([running, finished]);
        await tty.DisposeAsync();
        Assert.Equal("unknown", tty.ChildAgents[0].Reference.State);
        Assert.Same(finished, tty.ChildAgents[1].Reference);
    });
}
