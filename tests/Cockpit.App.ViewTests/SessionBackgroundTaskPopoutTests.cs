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

    [Fact]
    public void SubAgentsAndShells_AreGroupedSeparately_AndCountedTogether() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        Assert.Equal(0, session.BackgroundTaskCount);
        Assert.Empty(session.BackgroundSubAgents);

        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1: review the diff"),
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2: write tests"),
            new BackgroundTask("b1", BackgroundTaskKind.Shell, "npm run dev")));

        Assert.True(session.HasBackgroundTasks);
        Assert.Equal(3, session.BackgroundTaskCount);
        Assert.Equal(2, session.BackgroundSubAgents.Count);
        Assert.Single(session.BackgroundShells);
        Assert.Empty(session.BackgroundOtherTasks);
        Assert.Equal("3 running", session.BackgroundTaskSummary);
        Assert.Equal("CockpitStatusBusyBrush", session.BackgroundSubAgents[0].StatusBrushKey);
        Assert.Equal("CockpitStatusWaitingBrush", session.BackgroundShells[0].StatusBrushKey);
        Assert.All(session.BackgroundSubAgents, row =>
        {
            Assert.Equal("Sub-agent", row.KindLabel);
            Assert.Equal("running", row.StatusText);
            Assert.Equal("unknown", row.LastToolText);
            Assert.Equal("unknown", row.TokensText);
        });

        var row = session.BackgroundSubAgents[0];
        session.Apply(Outstanding(
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Review updated") { LastTool = "Read", Tokens = 1234 },
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2: write tests")));
        Assert.Same(row, session.BackgroundSubAgents[0]);
        Assert.Equal("Review updated", row.Description);
        Assert.Equal("Read", row.LastToolText);
        Assert.Equal("1234", row.TokensText);
        session.Apply(new BackgroundTaskNotification
        {
            SessionId = "s1", TaskId = "a1", Status = BackgroundTaskStatus.Completed,
        });
        Assert.Equal("running", row.StatusText);
        Assert.Equal(2, session.BackgroundTaskCount);

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
        Assert.Equal("unknown", session.BackgroundOtherTasks[0].KindLabel);
        Assert.Equal("1 running", session.BackgroundTaskSummary);
    });

    [Fact]
    public void SingularCounts_ReadAsSingular_NotPlural() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        Assert.Equal("1 running", session.BackgroundTaskSummary);
        var tty = new TtyViewModel();
        tty.UpdateChildAgents([new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")]);
        Assert.Equal("1 running", tty.BackgroundTaskSummary);
        tty.UpdateChildAgents([
            new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1"),
            new BackgroundTask("a2", BackgroundTaskKind.SubAgent, "Agent 2"),
        ]);
        Assert.Equal("2 running", tty.BackgroundTaskSummary);
        var finished = new BackgroundTask("done", BackgroundTaskKind.SubAgent, "Finished agent") { State = "completed" };
        tty.UpdateChildAgents([tty.ChildAgents[0].Reference, tty.ChildAgents[1].Reference, finished]);
        Assert.Equal("2 running · 3 total", tty.BackgroundTaskSummary);
        session.Apply(new BackgroundTasksChanged
        {
            SessionId = "s1",
            Tasks = [new BackgroundTask("shell", BackgroundTaskKind.Shell, "Shell")],
            Children = [finished],
        });
        Assert.Equal("1 running · 2 total", session.BackgroundTaskSummary);
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
        Assert.Equal("0 running", session.BackgroundTaskSummary);
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

        Assert.Equal("unknown", session.BackgroundShells[0].Description);
        Assert.Equal("unknown", session.BackgroundShells[0].LastToolText);
        Assert.Equal("unknown", session.BackgroundShells[0].TokensText);
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.Shell, " ") { LastTool = " " }));
        Assert.Equal("unknown", session.BackgroundShells[0].Description);
        Assert.Equal("unknown", session.BackgroundShells[0].LastToolText);
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

    /// <summary>
    /// The row's own command is what the pop-out clicks (AC-630) — it replaced a PointerPressed handler in
    /// SessionView's code-behind, because the row template is now shared app-wide and a ResourceDictionary has no
    /// code-behind to hold one. Both windows resolve that one template; a second copy is what this must not become.
    /// </summary>
    [Fact]
    public void TheRowsOwnCommand_ExpandsIt_AndTheTemplateItDrawsWithIsSharedAppWide() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();
        session.Apply(Outstanding(new BackgroundTask("a1", BackgroundTaskKind.SubAgent, "Agent 1")));

        session.BackgroundSubAgents[0].ToggleCommand.Execute(null);
        Assert.True(session.BackgroundSubAgents[0].IsSelected);

        Assert.True(Avalonia.Application.Current!.Resources.TryGetResource(
            "BackgroundTaskRowTemplate", Avalonia.Styling.ThemeVariant.Dark, out var template));
        Assert.IsAssignableFrom<Avalonia.Controls.Templates.IDataTemplate>(template);
        var window = new Avalonia.Controls.Window
        {
            Width = 450, Height = 300,
            Content = new Cockpit.App.Controls.BackgroundWorkList { DataContext = session },
        };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var texts = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
            .OfType<Avalonia.Controls.TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToList();
        Assert.Contains("Sub-agent", texts);
        Assert.Contains("Agent 1", texts);
        Assert.Contains("running", texts);
        Assert.Contains("Last tool: unknown", texts);
        Assert.Contains("Tokens: unknown", texts);
        Assert.Contains("Time: 0:00", texts);
        window.Close();
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
