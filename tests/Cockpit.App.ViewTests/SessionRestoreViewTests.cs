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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-410 step 5 end-to-end: a cockpit that starts with one saved AI-session pane in <c>cockpit.json</c> shows it
/// after <see cref="CockpitViewModel.RestoreSessionPanesAsync"/> — without ever starting the session it describes.
/// "Started" here means the panel's own <c>Status</c>, which only a launch call
/// (<c>StartConfiguredAsync</c>/<c>LaunchConfigured</c>) changes from its "Not started." default.
/// </summary>
/// <remarks>
/// Tagged <c>[Collection("avalonia")]</c> only for <see cref="ReopenAndSendResume_OnARestoredKnownPane_StartsItWithTheSavedConversationId"/>
/// — the one test here whose path (<c>ScheduledResumeCoordinator.StartAsync</c>) touches
/// <c>Dispatcher.UIThread</c>, which hangs forever without the headless platform this collection's fixture sets up
/// (<see cref="HeadlessAvalonia"/>). Every other test in this class builds view-models only and does not need it.
/// </remarks>
[Collection("avalonia")]
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
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var coordinator = new Cockpit.Infrastructure.Agents.WorkspaceAgentCoordinator();
        var vm = NewVm(workspaceStore, stateStore, agentCoordinator: coordinator);

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();

        Assert.Single(vm.Sessions);
        var restored = vm.Sessions[0];
        Assert.Equal("saved-pane-1", restored.PaneId);
        Assert.Equal("webshop", restored.Title);
        Assert.True(restored.IsPaneVisible, "the restored pane belongs to the active Sessions workspace");
        Assert.True(coordinator.IsEnrolled(restored.PaneId));
        Assert.Null(coordinator.LastContactUtc(restored.PaneId));
        // Nothing started: the launch path (_LaunchSessionFromResultAsync/_StartSessionAsync) is the only place
        // that ever sets LaunchResult or a process id, and the restore path never calls it.
        Assert.Null(restored.LaunchResult);
        Assert.Null(restored.ProcessId);
    }

    // AC-514: a name that changes after the pane already exists — a plugin/agent suggestion, or an operator's
    // inline rename — used to live only on the view model, so the pane record a restart reads back still carried
    // whatever title it was created with. These prove the fix's other half: what SuggestName/CommitRename write
    // now actually lands where a restart reads from.
    [Fact]
    public async Task SuggestName_OnAPersistedSession_WritesTheNewTitleToThePaneRecord()
    {
        var pane = new WorkspacePane("saved-pane-1", PaneKind.AiSession) { ProfileId = "work", Title = "work - 1", NameIsChosen = false };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);
        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = vm.Sessions[0];

        var applied = restored.SuggestName("AC-514");

        Assert.True(applied);
        var persisted = vm.Workspaces.Settings.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1");
        Assert.Equal("AC-514", persisted.Title);
        // The pivot this ticket exists to protect (acceptance criterion 3): a suggestion is remembered, not
        // "chosen" — NameIsChosen must stay false so a later, better suggestion can still replace it (#AC-324).
        Assert.False(persisted.NameIsChosen);
        // Not just the in-memory Settings — the write must actually reach the store, or a restart never sees it.
        await workspaceStore.Received().SaveAsync(
            Arg.Is<WorkspaceSettings>(saved => saved.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1").Title == "AC-514"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommitRename_OnAPersistedSession_WritesTheNewTitleAndNameIsChosenToThePaneRecord()
    {
        var pane = new WorkspacePane("saved-pane-1", PaneKind.AiSession) { ProfileId = "work", Title = "work - 1", NameIsChosen = false };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);
        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = vm.Sessions[0];

        restored.BeginRename();
        restored.EditTitle = "release work";
        restored.CommitRename();

        var persisted = vm.Workspaces.Settings.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1");
        Assert.Equal("release work", persisted.Title);
        // A manual rename is the operator's own word — NameIsChosen must flip true, unlike a mere suggestion above.
        Assert.True(persisted.NameIsChosen);
        await workspaceStore.Received().SaveAsync(
            Arg.Is<WorkspaceSettings>(saved => saved.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1").Title == "release work"),
            Arg.Any<CancellationToken>());
    }

    // AC-514: SetSessionName (#AC-13 — the pane-id rename API a plugin or the cockpit's own UI calls through
    // ICockpitHost) used to set Title/HasGeneratedName directly, bypassing SessionPanelViewModel entirely — the
    // same bug as CommitRename, on a different call site the first pass of this fix missed. Proves it now goes
    // through SetNameDirectly and reaches the persisted pane like every other naming route.
    [Fact]
    public async Task SetSessionName_OnAPersistedSession_WritesTheNewTitleAndNameIsChosenToThePaneRecord()
    {
        var pane = new WorkspacePane("saved-pane-1", PaneKind.AiSession) { ProfileId = "work", Title = "work - 1", NameIsChosen = false };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

        var vm = NewVm(workspaceStore, stateStore);
        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = vm.Sessions[0];

        var applied = vm.SetSessionName(restored.PaneId, "AC-514 done");

        Assert.True(applied);
        var persisted = vm.Workspaces.Settings.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1");
        Assert.Equal("AC-514 done", persisted.Title);
        Assert.True(persisted.NameIsChosen);
        await workspaceStore.Received().SaveAsync(
            Arg.Is<WorkspaceSettings>(saved => saved.Workspaces.Single().Panes.Single(p => p.Id == "saved-pane-1").Title == "AC-514 done"),
            Arg.Any<CancellationToken>());
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
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

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

        // A directory that exists: AC-539 refuses to offer a resume into one that no longer does.
        var state = new SessionStateRecord("known-sdk", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, Path.GetTempPath(), null, null, "default", DateTimeOffset.UtcNow);
        var ttyState = state with { PaneId = "known-tty" };
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state, ttyState });
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state, ttyState });

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

    /// <summary>
    /// AC-513: the seam between <see cref="RestoreSessionPanesAsync"/> and <see cref="SessionStateRecorder"/> —
    /// found on review after the recorder's own unit tests (which never exercise <c>RestoreSessionPanesAsync</c>)
    /// and the recorder's throwaway harness (which never calls <c>Seed</c>) both stayed green while this path
    /// still broke. Uses a real <see cref="SessionStateStore"/> made genuinely unreadable (<c>chmod 0200</c>, the
    /// sharpest real case) and a real <see cref="SessionStateRecorder"/> wired into a real
    /// <see cref="CockpitViewModel"/> — a substitute for either would not reproduce this, since the bug is in how
    /// the two classes hand a load result to each other, not in either class alone.
    /// <para>
    /// Sequence: restore runs while the file cannot be read (must not latch the recorder onto a blank cache); a
    /// write while it is still unreadable must not corrupt the file either (criterion 2, already covered
    /// elsewhere, exercised here for completeness); then, once the file is readable again, a write must still
    /// find the saved id — proving the restore's failed <c>Seed</c> left the write path free to self-heal instead
    /// of a bad seed permanently blinding it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RestoreSessionPanesAsync_UnreadableStateFile_DoesNotBlindTheRecorderToTheSavedConversationId()
    {
        // Unix-only, same as AuditTrailPermissionTests: Windows has no mode bits, so there is no way to make a
        // file genuinely unreadable-but-appendable the way chmod 0200 does here.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"session-restore-unreadable-{Guid.NewGuid():N}.jsonl");
        try
        {
            var realStore = new SessionStateStore(path, NullLogger<SessionStateStore>.Instance);
            await realStore.RecordAsync(new SessionStateRecord(
                "pane-1", "work", "ClaudeCli", "conv-old", SessionConversationIdState.Known,
                Path.GetTempPath(), null, null, "default", DateTimeOffset.UtcNow));

            var pane = new WorkspacePane("pane-1", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
            var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
            var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };
            var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
            workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

            var recorder = new SessionStateRecorder(realStore, new SessionConversationTracker(), NullLogger<SessionStateRecorder>.Instance);
            var vm = NewVm(workspaceStore, realStore, sessionStateRecorder: recorder);

            File.SetUnixFileMode(path, UnixFileMode.UserWrite);
            try
            {
                await vm.Workspaces.InitializeAsync();
                await vm.RestoreSessionPanesAsync();

                // Still unreadable: this write must be skipped (criterion 2), not compose against a blank cache.
                await recorder.RecordPermissionModeChangedAsync("pane-1", "acceptEdits");
            }
            finally
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // The file is readable again — as it would be once whatever made it unreadable (a permissions
            // hiccup, a concurrent reader) clears. A write now must find the saved id via the write path's own
            // self-heal, which only still works if RestoreSessionPanesAsync's failed Seed left it alone.
            await recorder.RecordPermissionModeChangedAsync("pane-1", "bypassPermissions");

            var record = Assert.Single(await realStore.LoadAsync());
            Assert.Equal("conv-old", record.ConversationId);
            Assert.Equal(SessionConversationIdState.Known, record.ConversationState);
            Assert.Equal("bypassPermissions", record.PermissionMode);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(path);
            }
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
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state, ttyState });

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
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

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

    /// <summary>
    /// AC-1080: the provider picking its own conversation back up says nothing about the window, which comes up
    /// empty — the restore leaves the operator looking at a blank pane that is nonetheless mid-conversation. So a
    /// resume repaints from the log Cockpit itself keeps (AC-1090), the route the assistant already took alone.
    /// </summary>
    [Fact]
    public async Task ResumeConversation_OnARestoredPane_RepaintsWhatCockpitRecorded()
    {
        var root = _NewTranscriptRoot();
        try
        {
            var store = new SessionTranscriptLog(root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
            await store.AppendAsync("known-pane", _Recorded("what came before the crash"));
            await store.FlushAsync(CancellationToken.None);

            var (_, restored, _) = await _RestoreOneKnownPaneAsync(transcriptStore: store);
            restored.ResumeConversationCommand.Execute(null);
            await _SettleAsync(() => ((SessionViewModel)restored).Transcript.Count > 0);

            Assert.Equal("what came before the crash", Assert.Single(((SessionViewModel)restored).Transcript).Text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The other half: "Start fresh" is a new conversation, so it must not leave the old one in the log for the
    /// next resume to repaint. Rolled aside rather than dropped, and by the same store every pane shares — the
    /// assistant included — so the number of generations kept cannot differ per kind of session.
    /// </summary>
    [Fact]
    public async Task StartFresh_OnARestoredPane_RollsTheRecordedConversationAside()
    {
        var root = _NewTranscriptRoot();
        try
        {
            var store = new SessionTranscriptLog(root, NullLogger<SessionTranscriptLog>.Instance, TimeSpan.Zero);
            await store.AppendAsync("known-pane", _Recorded("the conversation before this one"));
            await store.FlushAsync(CancellationToken.None);

            var (_, restored, _) = await _RestoreOneKnownPaneAsync(transcriptStore: store);
            restored.StartFreshCommand.Execute(null);
            await _SettleAsync(() => !File.Exists(store.LogPath("known-pane")));

            Assert.False(File.Exists(store.LogPath("known-pane")), "the fresh conversation starts on an empty log");
            var archive = Assert.Single(Directory.GetFiles(root, "known-pane.previous-*.jsonl"));
            Assert.Contains("the conversation before this one", await File.ReadAllTextAsync(archive));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A log that is there but unreadable must not repaint as nothing: the session starts either way, so silence
    /// leaves the operator facing an empty window that reads as "this pane has no history" (AC-513's distinction,
    /// one level down). The row saying so is the only thing between that and the silent failures this epic keeps
    /// producing.
    /// </summary>
    [Fact]
    public async Task ResumeConversation_WhenTheRecordedLogCannotBeRead_SaysSoInsteadOfShowingNothing()
    {
        var store = Substitute.For<ISessionTranscriptStore>();
        store.TryLoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<IReadOnlyList<TranscriptSnapshotEntry>?>(_ => null);

        var (_, restored, _) = await _RestoreOneKnownPaneAsync(transcriptStore: store);
        restored.ResumeConversationCommand.Execute(null);
        await _SettleAsync(() => ((SessionViewModel)restored).Transcript.Count > 0);

        var row = Assert.Single(((SessionViewModel)restored).Transcript);
        Assert.Equal(TranscriptEntryKind.Error, row.Kind);
        Assert.Contains("could not be read back", row.Text);
    }

    private static string _NewTranscriptRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static TranscriptSnapshotEntry _Recorded(string text) =>
        new(Guid.NewGuid().ToString("n"), nameof(TranscriptEntryKind.UserText), text, null, null, null, null, false, DateTimeOffset.UtcNow);

    // RestoreDecided's handler is fire-and-forget, and unlike the launch calls around it the transcript work
    // touches a real file — so it does not finish inside Execute the way the rest of this chain does.
    private static async Task _SettleAsync(Func<bool> done)
    {
        for (var poll = 0; poll < 100 && !done(); poll++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task StartFresh_OnARestoredManagedWorktree_ReattachesBeforeStarting()
    {
        var record = new WorktreeRecord("known-pane", "/repo", "/repo-worktrees/known-pane", "cockpit/known", "abc123", DateTimeOffset.UtcNow);
        var worktrees = Substitute.For<IWorktreeManager>();
        worktrees.ListAsync(Arg.Any<CancellationToken>()).Returns([record]);
        worktrees.ReattachAsync(record.Path, "known-pane", Arg.Any<CancellationToken>()).Returns(record);
        var (_, restored, _) = await _RestoreOneKnownPaneAsync(worktrees);

        restored.StartFreshCommand.Execute(null);

        await worktrees.Received(1).ReattachAsync(record.Path, "known-pane", Arg.Any<CancellationToken>());
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
    /// AC-290 end-to-end: a scheduled resume due after a restart finds its pane only offering a restore (no runtime
    /// yet, so <c>CanTakeAPrompt</c> is false), and reopens it through
    /// <see cref="ScheduledResumeCoordinator.ReopenAndSend"/> exactly the way "Resume conversation" would — same
    /// conversation id, same profile. <c>ResolveSession</c> (wired by <see cref="CockpitViewModel.StartScheduledResumesAsync"/>,
    /// same as production) does find the pane; it is <c>CanTakeAPrompt</c> being false on a pane that was only just
    /// restored — never started — that refuses the direct send and forces the reopen path.
    /// </summary>
    [Fact]
    public Task ReopenAndSendResume_OnARestoredKnownPane_StartsItWithTheSavedConversationId() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (vm, restored, driver) = await _RestoreOneKnownPaneAsync();

        var store = Substitute.For<IScheduledResumeStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ScheduledResume>());
        vm.ScheduledResumes = new ScheduledResumeCoordinator(store);
        await vm.StartScheduledResumesAsync();

        await vm.ScheduledResumes.ScheduleAsync(new ScheduledResume(restored.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", "test"));
        await vm.ScheduledResumes.RunDueAsync(DateTimeOffset.Now);

        await driver.Received(1).StartAsync(
            WorkProfile, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Is<SessionResume?>(resume => resume != null && resume.Mode == SessionResumeMode.BySessionId && resume.SessionId == "conv-1"),
            Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.False(restored.HasRestoreOffer, "the banner disappears once the reopen lands, same as an operator-accepted resume");
    });

    /// <summary>
    /// AC-290's guard: a restored pane whose conversation cannot be resumed (wrong provider, worktree gone, no
    /// state at all) must never be started just because a scheduled resume came due. As above, <c>ResolveSession</c>
    /// finds the pane fine — it is <c>CanTakeAPrompt</c> being false (never started) that forces the reopen
    /// attempt, which is what is under test here.
    /// </summary>
    [Fact]
    public Task ReopenAndSendResume_WhenTheConversationCannotBeResumed_NeverStartsTheSession() => HeadlessAvalonia.RunAsync(async () =>
    {
        var pane = new WorkspacePane("unsupported-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("unsupported-pane", "work", "ollama", null, SessionConversationIdState.Unsupported, "/repo", null, null, null, DateTimeOffset.UtcNow);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

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
        Assert.False(restored.CanResumeConversation);

        var store = Substitute.For<IScheduledResumeStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ScheduledResume>());
        vm.ScheduledResumes = new ScheduledResumeCoordinator(store);
        await vm.StartScheduledResumesAsync();

        await vm.ScheduledResumes.ScheduleAsync(new ScheduledResume(restored.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", "test"));
        await vm.ScheduledResumes.RunDueAsync(DateTimeOffset.Now);

        await driver.DidNotReceive().StartAsync(
            Arg.Any<SessionProfile>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<IReadOnlySet<string>?>(), Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.True(restored.HasRestoreOffer, "the offer is left standing — nothing was started to clear it");
    });

    /// <summary>
    /// AC-290's other boundary: a TTY pane's <c>PromptSink</c> is wired asynchronously by the view once its pty has
    /// actually come up, well after a launch call already returns and the restore offer already clears — so a
    /// silent reopen-and-send could start the pty, destroy the offer, and still have nothing to send into. Until
    /// that can be waited on properly, the reopen path skips TTY panes outright: the resume falls back to the
    /// ordinary undelivered report, and — unlike the SDK case above — the offer is left standing rather than
    /// consumed by an attempt that could not finish.
    /// </summary>
    [Fact]
    public Task ReopenAndSendResume_OnARestoredTtyPane_IsSkipped_TheOfferStaysStanding() => HeadlessAvalonia.RunAsync(async () =>
    {
        var (vm, restored) = await _RestoreOneKnownTtyPaneAsync();

        var store = Substitute.For<IScheduledResumeStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ScheduledResume>());
        vm.ScheduledResumes = new ScheduledResumeCoordinator(store);
        await vm.StartScheduledResumesAsync();

        await vm.ScheduledResumes.ScheduleAsync(new ScheduledResume(restored.PaneId, DateTimeOffset.Now.AddMinutes(-1), "carry on", "test"));
        await vm.ScheduledResumes.RunDueAsync(DateTimeOffset.Now);

        Assert.True(restored.HasRestoreOffer, "a TTY reopen is skipped outright, so nothing ever started to clear it");
        Assert.True(restored.CanResumeConversation, "the offer is untouched, not degraded by a failed attempt");
    });

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
        var (_, restored) = await _RestoreOneKnownTtyPaneAsync();
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
        var (_, restored) = await _RestoreOneKnownTtyPaneAsync();
        var closed = false;
        restored.CloseRequested += (_, _) => closed = true;

        restored.ResumeConversationCommand.Execute(null);
        ((TtyViewModel)restored).OnLaunchSucceeded();

        ((TtyViewModel)restored).OnProcessExited("claude exited normally");

        Assert.True(closed, "the degrade window closed once the launch succeeded, so a later exit is an ordinary close");
    }

    private static async Task<(CockpitViewModel Vm, SessionPanelViewModel Restored)> _RestoreOneKnownTtyPaneAsync()
    {
        var pane = new WorkspacePane("known-tty-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Tty };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("known-tty-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, Path.GetTempPath(), null, null, "default", DateTimeOffset.UtcNow);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { WorkProfile });

        var vm = NewVm(workspaceStore, stateStore, new SessionRestorePlanner(profileStore));

        await vm.Workspaces.InitializeAsync();
        await vm.RestoreSessionPanesAsync();
        var restored = Assert.Single(vm.Sessions);
        Assert.True(restored.CanResumeConversation);

        return (vm, restored);
    }

    /// <summary>
    /// Shared setup for the two start-on-accept tests: one restored, SDK-kind pane whose plan is
    /// <see cref="SessionRestoreAvailability.Known"/> with conversation id "conv-1", wired to a fake
    /// <see cref="ISessionDriver"/> so the actual <c>StartAsync</c> call (and the resume it carries) can be observed.
    /// </summary>
    private static async Task<(CockpitViewModel Vm, SessionPanelViewModel Restored, ISessionDriver Driver)> _RestoreOneKnownPaneAsync(
        IWorktreeManager? worktreeManager = null,
        ISessionTranscriptStore? transcriptStore = null)
    {
        var pane = new WorkspacePane("known-pane", PaneKind.AiSession) { ProfileId = "work", SessionKind = PaneSessionKind.Sdk };
        var sessions = Workspace.Create("Work", WorkspaceType.Sessions).WithPane(pane);
        var settings = new WorkspaceSettings { Workspaces = [sessions], ActiveWorkspaceId = sessions.Id };

        var workspaceStore = Substitute.For<IWorkspaceSettingsStore>();
        workspaceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);

        var state = new SessionStateRecord("known-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, Path.GetTempPath(), null, null, "default", DateTimeOffset.UtcNow);
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

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
            worktreeManager: worktreeManager,
            sessionFactory: () => new SessionViewModel(new SessionManager(factory), transcriptStore: transcriptStore));

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

        var startedState = new SessionStateRecord("started-pane", "work", "claude-cli", "conv-1", SessionConversationIdState.Known, Path.GetTempPath(), null, null, "default", DateTimeOffset.UtcNow);
        var unstartedState = startedState with { PaneId = "unstarted-pane" };
        var stateStore = Substitute.For<ISessionStateStore>();
        stateStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { startedState, unstartedState });
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { startedState, unstartedState });

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
        stateStore.TryLoadAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<SessionStateRecord>());

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
        ISessionDialogService? dialogService = null,
        SessionStateRecorder? sessionStateRecorder = null,
        Cockpit.Core.Abstractions.Agents.IWorkspaceAgentCoordinator? agentCoordinator = null)
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
            worktreeManager: worktreeManager,
            sessionStateRecorder: sessionStateRecorder,
            agentCoordinator: agentCoordinator);
    }
}
