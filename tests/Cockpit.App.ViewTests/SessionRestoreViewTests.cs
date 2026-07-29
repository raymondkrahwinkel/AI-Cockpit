using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Sessions;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Core.Workspaces;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-410 step 5 end-to-end: a cockpit that starts with one saved AI-session pane in <c>cockpit.json</c> shows it
/// after <see cref="CockpitViewModel.RestoreSessionPanesAsync"/> — without ever starting the session it describes.
/// "Started" here means the panel's own <c>Status</c>, which only a launch call
/// (<c>StartConfiguredAsync</c>/<c>LaunchConfigured</c>) changes from its "Not started." default.
/// </summary>
public class SessionRestoreViewTests
{
    private static readonly SessionProfile WorkProfile = new("work", new ClaudeConfig(@"C:\fake\.claude"));

    [Fact]
    public async Task RestoreSessionPanesAsync_OneSavedPane_ShowsItWithoutStartingIt()
    {
        var pane = new WorkspacePane("saved-pane-1", PaneKind.AiSession)
        {
            ProfileId = "work",
            Title = "webshop",
            NameIsChosen = true,
            SessionKind = PaneSessionKind.Sdk,
        };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        Assert.Single(vm.Sessions);
        var restored = vm.Sessions[0];
        Assert.Equal("saved-pane-1", restored.PaneId);
        Assert.Equal("webshop", restored.Title);
        Assert.True(restored.IsPaneVisible, "the restored pane belongs to the active Sessions workspace");
        // Nothing started: the launch path (_LaunchSessionFromResultAsync/_StartSessionAsync) is the only place
        // that ever sets LaunchResult or a process id, and the restore path never calls it.
        Assert.Null(restored.LaunchResult);
        Assert.Null(restored.ProcessId);
    }

    [Fact]
    public async Task RestoreSessionPanesAsync_TwoPanesWithTheSameHandEditedId_MaterializesOnlyOne()
    {
        var duplicate = new WorkspacePane("dup-1", PaneKind.AiSession) { ProfileId = "work" };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions) with { Panes = [duplicate, duplicate with { }] };
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        Assert.Single(vm.Sessions);
    }

    /// <summary>
    /// The restore-offer banner (AC-410 design decision 5): the same properties drive both <c>SessionView</c> and
    /// <c>TtyView</c>'s banner bindings (<c>HasRestoreOffer</c>/<c>CanResumeConversation</c>), so restoring one pane
    /// of each kind against a <see cref="SessionConversationIdState.Known"/> state exercises both views' binding
    /// surface identically. "Resume conversation" must show for both.
    /// </summary>
    [Fact]
    public async Task RestoreSessionPanesAsync_KnownConversation_OffersResumeOnBothSdkAndTtyPanes()
    {
        var sdkPane = new WorkspacePane("known-sdk", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var ttyPane = new WorkspacePane("known-tty", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(sdkPane).WithPane(ttyPane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("known-sdk", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, "/repo", null, null, "default", DateTimeOffset.UtcNow);
        var ttyState = state with { PaneId = "known-tty" };
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state, ttyState });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var vm = NewVm(workspaceStore, stateStore, new SessionRestorePlanner(profileStore));

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        Assert.Equal(2, vm.Sessions.Count);
        foreach (var restored in vm.Sessions)
        {
            Assert.True(restored.HasRestoreOffer, $"{restored.PaneId} should show the restore banner");
            Assert.True(restored.CanResumeConversation, $"{restored.PaneId} should offer Resume conversation");
            Assert.False(string.IsNullOrEmpty(restored.RestoreOfferText));
            Assert.Equal(string.Empty, restored.RestoreDegradedReason);
        }
    }

    /// <summary>An <see cref="SessionConversationIdState.Unsupported"/> provider hides "Resume conversation" on both pane kinds.</summary>
    [Fact]
    public async Task RestoreSessionPanesAsync_UnsupportedProvider_HidesResumeOnBothSdkAndTtyPanes()
    {
        var sdkPane = new WorkspacePane("unsupported-sdk", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var ttyPane = new WorkspacePane("unsupported-tty", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(sdkPane).WithPane(ttyPane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("unsupported-sdk", "work", "ollama", null, SessionConversationIdState.Unsupported, "/repo", null, null, null, DateTimeOffset.UtcNow);
        var ttyState = state with { PaneId = "unsupported-tty" };
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state, ttyState });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var vm = NewVm(workspaceStore, stateStore, new SessionRestorePlanner(profileStore));

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        Assert.Equal(2, vm.Sessions.Count);
        foreach (var restored in vm.Sessions)
        {
            Assert.True(restored.HasRestoreOffer, $"{restored.PaneId} should still show the restore banner");
            Assert.False(restored.CanResumeConversation, $"{restored.PaneId} should not offer Resume conversation");
            Assert.False(string.IsNullOrEmpty(restored.RestoreDegradedReason));
        }
    }

    /// <summary>
    /// AC-410's documented pitfall: <c>WorktreeBranch</c> must be set from the worktree registry at materialization
    /// time (inside <c>RestoreSessionPanesAsync</c>), not left for the start path — the restore path runs with
    /// <c>IsolateInWorktree: false</c>, so <c>_ResolveIsolatedWorkingDirectoryAsync</c> never gets a chance to
    /// resolve it. Asserted before any start happens.
    /// </summary>
    [Fact]
    public async Task RestoreSessionPanesAsync_MatchingWorktreeRecord_SetsWorktreeBranchBeforeAnyStart()
    {
        var pane = new WorkspacePane("wt-pane", PaneKind.AiSession) { ProfileId = "work" };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var record = new WorktreeRecord("wt-pane", "/repo", "/repo-worktrees/wt-pane", "cockpit/wt-pane", "abc123", DateTimeOffset.UtcNow);
        var worktreeManager = Substitute.For<IWorktreeManager>();
        worktreeManager.ListAsync(Arg.Any<CancellationToken>()).Returns(new[] { record });

        var vm = NewVm(workspaceStore, stateStore, worktreeManager: worktreeManager);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        var restored = Assert.Single(vm.Sessions);
        Assert.Equal("cockpit/wt-pane", restored.WorktreeBranch);
        // Still nothing started — the branch is known before any launch call runs.
        Assert.Null(restored.LaunchResult);
        Assert.Null(restored.ProcessId);
    }

    /// <summary>"Start fresh" starts the restored pane with <c>SessionResume.New</c>, whatever the plan knew about an earlier conversation.</summary>
    [Fact]
    public async Task StartFresh_OnARestoredPane_StartsWithSessionResumeNew()
    {
        var (_, restored, driver) = await _RestoreOneKnownPaneAsync();

        // RestoreDecided's handler is fire-and-forget (OnSessionCloseRequested's own pattern — see
        // CockpitViewModelTests.SessionCloseRequested_ClosesThatSessionThroughTheCockpit), but every awaited step
        // underneath it resolves against an already-completed fake Task, so the whole chain runs synchronously
        // within this call.
        restored.StartFreshCommand.Execute(null);

        await driver.Received(1).StartAsync(
            WorkProfile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Is<SessionResume?>(resume => resume != null && resume.Mode == SessionResumeMode.New),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.False(restored.HasRestoreOffer, "the banner disappears once the start actually lands");
    }

    /// <summary>"Resume conversation" starts the restored pane with the conversation id the saved state recorded.</summary>
    [Fact]
    public async Task ResumeConversation_OnARestoredPane_StartsWithTheSavedConversationId()
    {
        var (_, restored, driver) = await _RestoreOneKnownPaneAsync();

        restored.ResumeConversationCommand.Execute(null);

        await driver.Received(1).StartAsync(
            WorkProfile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Is<SessionResume?>(resume => resume != null && resume.Mode == SessionResumeMode.BySessionId && resume.SessionId == "conv-1"),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.False(restored.HasRestoreOffer);
    }

    /// <summary>
    /// AC-410's biggest risk, per the design doc: <c>TtyViewModel.OnProcessExited</c> used to close the pane
    /// unconditionally, so a resume that fails fast — <c>claude --resume &lt;expired-id&gt;</c> printing an error
    /// and exiting immediately — deleted the very pane record it was trying to bring back, in the same run that
    /// just restored it. Within the degrade window this must not happen: the offer comes back instead, with the
    /// last visible output as the reason, and "Start fresh" one click away.
    /// </summary>
    [Fact]
    public async Task TtyResumeThatExitsImmediately_DoesNotClosePane_ButOffersItBackWithTheReason()
    {
        var restored = await _RestoreOneKnownTtyPaneAsync();
        var closed = false;
        restored.CloseRequested += (_, _) => closed = true;

        restored.ResumeConversationCommand.Execute(null);
        Assert.False(restored.HasRestoreOffer, "the offer clears the moment the launch is configured, same as any other start");

        ((TtyViewModel)restored).OnProcessExited("No conversation found with session ID: 00000000-dead-beef-0000-000000000000");

        Assert.False(closed, "an exit within the degrade window must not close the pane, or its cockpit.json record goes with it");
        Assert.True(restored.HasRestoreOffer, "the offer comes back so the operator can still start fresh");
        Assert.NotNull(restored.RestoreOffer);
        Assert.Equal(SessionRestoreAvailability.Gone, restored.RestoreOffer!.Availability);
        Assert.False(restored.CanResumeConversation, "the resume that just failed must not be offered again as if nothing happened");
        Assert.Contains("No conversation found with session ID", restored.RestoreDegradedReason);
    }

    /// <summary>Once a launch actually got the TUI on screen, an exit afterwards is the operator closing claude, not a resume failing before it started — the ordinary close path applies again.</summary>
    [Fact]
    public async Task TtyResumeThatLaunchesSuccessfully_ClosesNormallyOnALaterExit()
    {
        var restored = await _RestoreOneKnownTtyPaneAsync();
        var closed = false;
        restored.CloseRequested += (_, _) => closed = true;

        restored.ResumeConversationCommand.Execute(null);
        ((TtyViewModel)restored).OnLaunchSucceeded();

        ((TtyViewModel)restored).OnProcessExited("claude exited normally");

        Assert.True(closed, "the degrade window closed once the launch succeeded, so a later exit is an ordinary close");
    }

    private static async Task<SessionPanelViewModel> _RestoreOneKnownTtyPaneAsync()
    {
        var pane = new WorkspacePane("known-tty-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("known-tty-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, "/repo", null, null, "default", DateTimeOffset.UtcNow);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var vm = NewVm(workspaceStore, stateStore, new SessionRestorePlanner(profileStore));

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = Assert.Single(vm.Sessions);
        Assert.True(restored.CanResumeConversation);

        return restored;
    }

    /// <summary>
    /// Shared setup for the two start-on-accept tests: one restored, SDK-kind pane whose plan is
    /// <see cref="SessionRestoreAvailability.Known"/> with conversation id "conv-1", wired to a fake
    /// <see cref="ISessionDriver"/> so the actual <c>StartAsync</c> call (and the resume it carries) can be observed.
    /// </summary>
    private static async Task<(CockpitViewModel Vm, SessionPanelViewModel Restored, ISessionDriver Driver)> _RestoreOneKnownPaneAsync()
    {
        var pane = new WorkspacePane("known-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("known-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, "/repo", null, null, "default", DateTimeOffset.UtcNow);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyEvents());
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);

        var vm = NewVm(
            workspaceStore,
            stateStore,
            new SessionRestorePlanner(profileStore),
            sessionFactory: () => new SessionViewModel(new SessionManager(factory)));

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = Assert.Single(vm.Sessions);
        Assert.True(restored.CanResumeConversation);

        return (vm, restored, driver);
    }

    /// <summary>
    /// AC-410: the close-workspace confirmation used to count every session in <see cref="Workspace.Panes"/> as
    /// something the close "will be stopped" — true once, when a session on a desk always had a runtime. A
    /// restored pane still showing its offer has none, so folding it into that count makes the sentence false. The
    /// two are named apart instead.
    /// </summary>
    [Fact]
    public async Task CloseWorkspaceWithConfirmationAsync_MixOfStartedAndUnstartedPanes_NamesThemSeparately()
    {
        var startedPane = new WorkspacePane("started-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var unstartedPane = new WorkspacePane("unstarted-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(startedPane).WithPane(unstartedPane);
        // A second workspace: CanClose refuses to close the last one standing, and this test needs to reach the
        // confirmation dialog rather than being turned away before it.
        var other = Workspace.Create("Other", WorkspaceType.Sessions);
        var settings = new WorkspaceSettings { Workspaces = [sessions, other], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var startedState = new SessionStateRecord("started-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, "/repo", null, null, "default", DateTimeOffset.UtcNow);
        var unstartedState = startedState with { PaneId = "unstarted-pane" };
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { startedState, unstartedState });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var dialogService = Substitute.For<ISessionDialogService>();
        string? confirmationMessage = null;
        dialogService.ShowConfirmationDialogAsync(Arg.Any<string>(), Arg.Do<string>(m => confirmationMessage = m), Arg.Any<string>())
            .Returns(false); // Declining is enough — only the wording is under test, not the actual close.

        var vm = NewVm(workspaceStore, stateStore, new SessionRestorePlanner(profileStore), dialogService: dialogService);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        Assert.Equal(2, vm.Sessions.Count);

        // Starts the one pane, leaving the other still only offering.
        var toStart = vm.Sessions.Single(s => s.PaneId == "started-pane");
        toStart.ResumeConversationCommand.Execute(null);
        Assert.False(toStart.HasRestoreOffer);
        Assert.True(vm.Sessions.Single(s => s.PaneId == "unstarted-pane").HasRestoreOffer);

        await vm.CloseWorkspaceWithConfirmationAsync(sessions.Id);

        Assert.NotNull(confirmationMessage);
        Assert.Contains("1 session, which will be stopped", confirmationMessage);
        Assert.Contains("1 restored session that never started", confirmationMessage);
    }

    /// <summary>
    /// AC-410: <c>CockpitViewModel.DisposeAsync</c> disposes every session in <see cref="CockpitViewModel.Sessions"/>
    /// on shutdown, including a restored one that was never started — no runtime, no pty. Documented in the design
    /// as "probably null-safe, not verified"; this is that verification, for both pane kinds.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_WithARestoredNeverStartedPane_DoesNotThrow()
    {
        var sdkPane = new WorkspacePane("never-started-sdk", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var ttyPane = new WorkspacePane("never-started-tty", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(sdkPane).WithPane(ttyPane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        Assert.Equal(2, vm.Sessions.Count);
        Assert.All(vm.Sessions, s => Assert.Null(s.LaunchResult));

        await vm.DisposeAsync();
    }

    private static async IAsyncEnumerable<SessionEvent> _EmptyEvents([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static CockpitViewModel NewVm(
        IWorkspaceSettingsStore? workspaceSettingsStore,
        ISessionStateStore? sessionStateStore,
        SessionRestorePlanner? sessionRestorePlanner = null,
        IWorktreeManager? worktreeManager = null,
        Func<SessionViewModel>? sessionFactory = null,
        ISessionDialogService? dialogService = null)
    {
        dialogService ??= Substitute.For<ISessionDialogService>();
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<Cockpit.Core.Abstractions.Terminal.ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new Cockpit.Core.Terminal.TerminalSettings());

        return new CockpitViewModel(
            sessionFactory ?? (() => new SessionViewModel()),
            () => new TtyViewModel(),
            dialogService,
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            workspaceSettingsStore: workspaceSettingsStore,
            sessionStateStore: sessionStateStore,
            sessionRestorePlanner: sessionRestorePlanner,
            worktreeManager: worktreeManager);
    }
}
