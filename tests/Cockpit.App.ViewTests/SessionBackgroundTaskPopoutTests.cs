using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The background-work button and pop-out (AC-531): a count/collection built on top of AC-276's own
/// <c>BackgroundTasksChanged</c> ledger, not a second one. The one fact these tests exist to pin down is the gap
/// this ticket closed — AC-532's review confirmed that <see cref="SessionViewModel.TurnCompleted"/> clears the
/// composer's tool-activity band unconditionally, but a detached sub-agent or shell does not end with the turn,
/// so this surface must survive exactly the event that clears everything else.
/// </summary>
[Collection("avalonia")]
public class SessionBackgroundTaskPopoutTests
{
    private static BackgroundTasksChanged Outstanding(params BackgroundTask[] tasks) =>
        new() { SessionId = "s1", Tasks = tasks };

    private static TurnCompleted Turn() =>
        new() { SessionId = "s1", Subtype = "success", Result = "done", IsError = false };

    [Fact]
    public void NoBackgroundTasks_CountAndCollectionsAreEmpty_AndNoGroupIsShown() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        Assert.False(session.HasBackgroundTasks);
        Assert.Equal(0, session.BackgroundTaskCount);
        Assert.Empty(session.BackgroundSubAgents);
        Assert.Empty(session.BackgroundShells);
        Assert.Empty(session.BackgroundOtherTasks);
        Assert.False(session.HasBackgroundSubAgents);
        Assert.False(session.HasBackgroundShells);
        Assert.False(session.HasBackgroundOtherTasks);
        Assert.Equal("nothing", session.BackgroundTaskSummary);
    });

    [Fact]
    public void SubAgentsAndShells_AreGroupedSeparately_AndCountedTogether() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1: review the diff"),
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2: write tests"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        Assert.True(session.HasBackgroundTasks);
        Assert.Equal(3, session.BackgroundTaskCount);
        Assert.Equal(2, session.BackgroundSubAgents.Count);
        Assert.Single(session.BackgroundShells);
        Assert.Empty(session.BackgroundOtherTasks);
        Assert.Equal("2 sub-agents · 1 shell", session.BackgroundTaskSummary);

        // Distinguishable by kind colour (AC-531 #3) — no per-task status exists in the contract, so this is
        // derived purely from Kind.
        Assert.Equal("CockpitStatusBusyBrush", session.BackgroundSubAgents[0].StatusBrushKey);
        Assert.Equal("CockpitStatusWaitingBrush", session.BackgroundShells[0].StatusBrushKey);
    });

    [Fact]
    public void AnUnrecognisedKind_IsCountedInTheBadge_ButKeptOutOfTheNamedGroups() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(new BackgroundTask("x1", BackgroundTaskKind.Unknown, "something new")));

        Assert.Equal(1, session.BackgroundTaskCount);
        Assert.Empty(session.BackgroundSubAgents);
        Assert.Empty(session.BackgroundShells);
        Assert.Single(session.BackgroundOtherTasks);
        Assert.Equal("1 other", session.BackgroundTaskSummary);
    });

    [Fact]
    public void SingularCounts_ReadAsSingular_NotPlural() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        Assert.Equal("1 sub-agent", session.BackgroundTaskSummary);
    });

    [Fact]
    public void ATaskThatDisappearsFromTheList_IsRemovedFromItsGroup() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));
        Assert.Single(session.BackgroundSubAgents);

        // The driver restates the whole set; the last one ending arrives as an empty list, not a delta.
        session.Apply(Outstanding());

        Assert.Empty(session.BackgroundSubAgents);
        Assert.False(session.HasBackgroundTasks);
        Assert.Equal(0, session.BackgroundTaskCount);
    });

    [Fact]
    public void ASelectedTaskThatDisappears_DoesNotThrow_AndTheOtherRowStaysUnaffected() => HeadlessAvalonia.Run(() =>
    {
        // The hostile case named in the ticket's own harness: a task finishes while its detail is expanded in an
        // open pop-out. Nothing here reads "is the pop-out currently open" — this only has to prove the VM side
        // (row removal, no crash) copes while a row is mid-expansion.
        var session = new SessionViewModel();
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2")));
        session.ToggleBackgroundTaskSelection(session.BackgroundSubAgents[0]);
        Assert.True(session.BackgroundSubAgents[0].IsSelected);

        // "a1" (the selected one) finishes; "a2" keeps running.
        session.Apply(Outstanding(new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2")));

        Assert.Single(session.BackgroundSubAgents);
        Assert.Equal("a2", session.BackgroundSubAgents[0].TaskId);
        Assert.False(session.BackgroundSubAgents[0].IsSelected, "the surviving row was never selected itself");
    });

    [Fact]
    public void AnAbsurdlyLongDescription_DoesNotThrow_AndIsCarriedInFull() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var longDescription = new string('x', 20_000);

        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, longDescription)));

        Assert.Equal(longDescription, session.BackgroundSubAgents[0].Description);
    });

    [Fact]
    public void ANullDescription_FallsBackToALabel_RatherThanRenderingBlank() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.Shell, null)));

        Assert.Equal("(no description)", session.BackgroundShells[0].Description);
    });

    [Fact]
    public void TensOfOutstandingTasks_AreAllCarried() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        var tasks = Enumerable.Range(0, 40)
            .Select(i => new BackgroundTask($"t{i}", i % 2 == 0 ? BackgroundTaskKind.SubAgent : BackgroundTaskKind.Shell, $"task {i}"))
            .ToArray();

        session.Apply(Outstanding(tasks));

        Assert.Equal(40, session.BackgroundTaskCount);
        Assert.Equal(20, session.BackgroundSubAgents.Count);
        Assert.Equal(20, session.BackgroundShells.Count);
    });

    // --- The gap this ticket closes (AC-532's review flagged it explicitly) ---

    [Fact]
    public void TurnCompleting_WithNoBackgroundWorkOutstanding_LeavesTheCountAtZero() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;

        session.Apply(Turn());

        Assert.False(session.HasBackgroundTasks);
        Assert.Equal(0, session.BackgroundTaskCount);
    });

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
    public void ASessionError_ClearsTheBackgroundList_SoACrashedSessionDoesNotShowPhantomWork() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.IsBusy = true;
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        session.Apply(new SessionError { SessionId = "s1", Message = "the driver died" });

        Assert.False(session.HasBackgroundTasks);
        Assert.Equal(0, session.BackgroundTaskCount);
        Assert.Empty(session.BackgroundSubAgents);
        Assert.Empty(session.BackgroundShells);
    });

    // --- Selection / click-through (AC-531 #4) ---

    [Fact]
    public void SelectingATask_ExpandsOnlyThatRow() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2")));

        session.ToggleBackgroundTaskSelection(session.BackgroundSubAgents[0]);

        Assert.True(session.BackgroundSubAgents[0].IsSelected);
        Assert.False(session.BackgroundSubAgents[1].IsSelected);

        session.ToggleBackgroundTaskSelection(session.BackgroundSubAgents[1]);

        Assert.False(session.BackgroundSubAgents[0].IsSelected, "selecting a second row must collapse the first");
        Assert.True(session.BackgroundSubAgents[1].IsSelected);
    });

    [Fact]
    public void SelectingTheSameTaskTwice_Collapses() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        session.ToggleBackgroundTaskSelection(session.BackgroundSubAgents[0]);
        Assert.True(session.BackgroundSubAgents[0].IsSelected);

        session.ToggleBackgroundTaskSelection(session.BackgroundSubAgents[0]);
        Assert.False(session.BackgroundSubAgents[0].IsSelected);
    });

    // --- Age derivation (AC-531 #8) ---

    [Fact]
    public void FirstObservation_StartsTheClockAtZero() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        Assert.Equal("0:00", session.BackgroundSubAgents[0].AgeText);
    });

    [Fact]
    public void ElapsedTime_FormatsAsMinutesColonSeconds_MatchingAC532sNotation()
    {
        // Same notation AC-532's composer activity band shipped, and the same formatter (SessionViewModel is
        // internal-visible to this test project) — not "1m 12s", the form the HTML mockup drew before the
        // notation had shipped anywhere.
        var row = new BackgroundTaskViewModel("a1", BackgroundTaskKind.SubAgent, "Agent 1", DateTimeOffset.Now.AddSeconds(-72));

        Assert.Equal("1:12", row.AgeText);
    }

    [Fact]
    public Task ATaskThatDisappearsAndReappears_StartsAFreshClock_RatherThanResumingTheOldOne() => HeadlessAvalonia.RunAsync(async () =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        // Let real time pass so a carried-over clock and a fresh one would visibly disagree.
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        session.Apply(Outstanding()); // "a1" finishes
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1 again"))); // same TaskId, reused

        Assert.Equal("0:00", session.BackgroundSubAgents[0].AgeText);
    });

    [Fact]
    public void RefreshBackgroundTaskAges_ReRaisesAgeTextForEveryOutstandingRow() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        var raisedFor = new List<string>();
        void OnSubAgentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => raisedFor.Add("sub:" + e.PropertyName);
        void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => raisedFor.Add("shell:" + e.PropertyName);
        session.BackgroundSubAgents[0].PropertyChanged += OnSubAgentChanged;
        session.BackgroundShells[0].PropertyChanged += OnShellChanged;

        session.RefreshBackgroundTaskAges();

        Assert.Contains("sub:AgeText", raisedFor);
        Assert.Contains("shell:AgeText", raisedFor);
    });
}
