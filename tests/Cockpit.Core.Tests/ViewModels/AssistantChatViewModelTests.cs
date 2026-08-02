using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="AssistantChatViewModel"/> against fakes of <see cref="IAssistantSessionHost"/> — the pop-out
/// window's own peephole contract, see that interface's remarks for why it exists — rather than the real
/// <c>AssistantSessionHost</c>, which needs a live <c>CockpitViewModel</c> to construct. Covers AC-543's three
/// hard requirements for this window: criterion 7 (closing never ends the assistant's session; reopening shows
/// what stood), criterion 8 (typing needs no STT/voice dependency at all), and criterion 9 (read-aloud off
/// interrupts whatever is already playing, not just the next reply).
/// </summary>
public class AssistantChatViewModelTests
{
    private static IAssistantSessionHost FakeHost(SessionViewModel? session = null, AssistantActivity activity = AssistantActivity.Ready)
    {
        var host = Substitute.For<IAssistantSessionHost>();
        host.Session.Returns(session);
        host.Activity.Returns(activity);
        host.EnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        host.RestartAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        host.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return host;
    }

    /// <summary>
    /// This window never restarts the assistant — not on any path it has. The restart lives beside the setting
    /// that needs it (Options → Voice → Assistant Profile), because "this applies at the next start" is only
    /// useful next to the thing that provides one.
    /// </summary>
    /// <remarks>
    /// Written against the host rather than against the absence of a command, so it keeps holding if a future
    /// header control grows one: whatever this window offers, it must not be the thing that ends the conversation
    /// it is showing.
    /// </remarks>
    [Fact]
    public async Task TheChatWindow_NeverRestartsTheAssistant_OnAnyPathItHas()
    {
        var session = new SessionViewModel();
        var host = FakeHost(session);
        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        await vm.EnsureOpenedAsync();
        vm.InputText = "what did AC-223 do overnight";
        await vm.SendCommand.ExecuteAsync(null);
        vm.SpeakReplies = false;
        await vm.ApplySettingsAsync();
        vm.Dispose();

        await host.DidNotReceive().RestartAsync(Arg.Any<CancellationToken>());
        Assert.Same(session, host.Session);
    }

    private static IAssistantSettingsStore FakeSettingsStore(bool speakReplies = true)
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true, SpeakReplies = speakReplies }));
        return store;
    }

    /// <summary>
    /// AC-575 criterion 5: the "some of these were never shown to you" mark has to be true while the window is open,
    /// not only at the moment it opened. The window is ownerless and kept between openings, so Options is reachable
    /// without it closing — and it only ever read the flag in <see cref="AssistantChatViewModel.EnsureOpenedAsync"/>,
    /// once per window lifetime. Turning the bypass on while the window sat there left it saying the opposite, in
    /// the state the operator is in most.
    /// </summary>
    [Fact]
    public async Task ABypassSwitchedOnWhileTheWindowIsOpen_ReachesTheHeaderOnTheSaveSignal()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true }));
        var vm = new AssistantChatViewModel(FakeHost(), store, Substitute.For<IVoicePlaybackQueue>());

        await vm.EnsureOpenedAsync();
        Assert.False(vm.ConsentBypassActive);

        // Options saves a bypass. The window is still the same instance — nothing reopened.
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantSettings { IsEnabled = true, ConsentBypassSources = ["Terminal MCP"] }));
        await vm.ApplySettingsAsync();

        Assert.True(vm.ConsentBypassActive);
    }

    [Fact]
    public void ARowArrivingInTheTranscript_RaisesHasMessages_SoTheWindowStopsShowingItsPlaceholder()
    {
        // The defect this pins, found by opening the app rather than by any test: the window binds its transcript
        // scroller to HasMessages and its "type a message to start talking" placeholder to the inverse, but
        // HasMessages was only ever re-raised when the *session* changed. At that instant the transcript is empty —
        // the session is set the moment it starts, and the first row does not exist until the turn makes one — so it
        // read false, nothing raised it again, and the window sat on its placeholder for the life of the session
        // while the assistant answered into a scroller nobody could see.
        var session = new SessionViewModel();
        // The parameterless constructor is the previewer's: it seeds sample rows so the Avalonia previewer and the
        // Screenshotter have something to draw. A real assistant session comes from the session factory and starts
        // empty, which is the state this defect lives in — so the sample is cleared to model the real thing.
        session.Transcript.Clear();
        var vm = new AssistantChatViewModel(FakeHost(session), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssistantChatViewModel.HasMessages)) { raised++; } };

        Assert.False(vm.HasMessages);

        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "wat is de status van AC-223"));

        Assert.True(vm.HasMessages);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Disposing_StopsWatchingTheTranscript_SoAClosedWindowIsNotKeptAliveByIt()
    {
        // The other half of the subscription: this window is a peephole that opens and closes repeatedly onto a
        // session that outlives all of them, so a watch that is never detached is one leaked view model per open.
        var session = new SessionViewModel();
        var vm = new AssistantChatViewModel(FakeHost(session), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AssistantChatViewModel.HasMessages)) { raised++; } };

        vm.Dispose();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "AC-223 draait op de review-desk"));

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Closing_NeverEndsTheSession_AndReopening_ShowsTheEarlierMessages()
    {
        // One host, standing across both "windows" — exactly what AssistantSessionHost is in the real app: a
        // singleton the window only ever reads from, never owns.
        var session = new SessionViewModel();
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "what did AC-223 do overnight"));
        var host = FakeHost(session);
        var playback = Substitute.For<IVoicePlaybackQueue>();

        var firstWindow = new AssistantChatViewModel(host, FakeSettingsStore(), playback);
        Assert.Contains(firstWindow.Session!.Transcript, entry => entry.Text.Contains("AC-223"));

        // "Closing" a window is exactly Dispose() (AssistantChatWindow.OnClosed calls nothing else on it), and
        // Dispose only detaches the peephole's own PropertyChanged subscription. The one member on the host that
        // does end a session — RestartAsync — this window never reaches at all (see
        // TheChatWindow_NeverRestartsTheAssistant_OnAnyPathItHas), which is what the assertion below is really
        // holding here: closing is not restarting.
        firstWindow.Dispose();
        host.DidNotReceive().RestartAsync(Arg.Any<CancellationToken>());
        Assert.Same(session, host.Session);
        Assert.NotEmpty(session.Transcript);

        // Reopening — a second view model against the same still-standing host, the same as a second chip click
        // building a new window over the same AssistantSessionHost singleton — shows the earlier conversation.
        var reopened = new AssistantChatViewModel(host, FakeSettingsStore(), playback);
        Assert.Contains(reopened.Session!.Transcript, entry => entry.Text.Contains("AC-223"));
    }

    [Fact]
    public async Task Sending_NeedsNoVoiceOrSttDependency()
    {
        var host = FakeHost();
        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.False(vm.SendCommand.CanExecute(null));

        // Typed text alone drives CanSend/Send — nothing here reads a microphone, a transcription service, or
        // any other voice dependency; the fakes above never see a call.
        vm.InputText = "what is the status of AC-223";
        Assert.True(vm.SendCommand.CanExecute(null));

        await vm.SendCommand.ExecuteAsync(null);

        await host.Received(1).SendAsync("what is the status of AC-223", Arg.Any<CancellationToken>());
        Assert.Equal(string.Empty, vm.InputText);
    }

    [Fact]
    public void WhitespaceInput_CannotBeSent()
    {
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        vm.InputText = "   ";

        Assert.False(vm.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task TurningReadAloudOff_InterruptsWhateverIsPlaying_AndPersists()
    {
        var settingsStore = FakeSettingsStore(speakReplies: true);
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var vm = new AssistantChatViewModel(FakeHost(), settingsStore, playback);

        // Loading the current setting on open must not itself read as a click — no interrupt for a value that
        // was never touched.
        await vm.EnsureOpenedAsync();
        Assert.True(vm.SpeakReplies);
        playback.DidNotReceive().StopAll();

        vm.SpeakReplies = false;

        // The same interrupt a push-to-talk barge-in uses (criterion 9: "uitzetten midden in een zin breekt af").
        playback.Received(1).StopAll();
        await settingsStore.Received(1).SaveAsync(
            Arg.Is<AssistantSettings>(s => s.SpeakReplies == false), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpeningWithReadAloudAlreadyStoredOff_DoesNotInterruptAnything()
    {
        var playback = Substitute.For<IVoicePlaybackQueue>();
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(speakReplies: false), playback);

        await vm.EnsureOpenedAsync();

        Assert.False(vm.SpeakReplies);
        playback.DidNotReceive().StopAll();
    }

    [Fact]
    public void UnavailableHost_SurfacesTheReasonRatherThanAnEmptyWindow()
    {
        var host = FakeHost(activity: AssistantActivity.Unavailable);
        host.UnavailableReason.Returns("The assistant is switched off. Turn it on in Options → Voice.");

        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.True(vm.IsUnavailable);
        Assert.Equal("The assistant is switched off. Turn it on in Options → Voice.", vm.UnavailableReason);
    }

    // AC-545 criterion 5: the flyout's own contract on this view model — load lazily, newest first, never throw
    // when nothing is wired up to read from.

    [Fact]
    public async Task NoSpawnAuditLogWired_LoadingDoesNothing_RatherThanThrowing()
    {
        // The optional constructor dependency: every construction path that predates this ticket (design-time
        // data, Screenshotter, the tests above) builds this view model without a fourth argument at all.
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        Assert.False(vm.HasSpawnLogEntries);
        Assert.True(vm.LoadSpawnLogCommand.CanExecute(null));
        var exception = await Record.ExceptionAsync(() => vm.LoadSpawnLogCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Empty(vm.SpawnLogEntries);
        Assert.False(vm.HasSpawnLogEntries);
    }

    [Fact]
    public async Task LoadingTheSpawnLog_ExposesTheTrailsEntries_NewestFirst()
    {
        var auditLog = Substitute.For<IAssistantSpawnAuditLog>();
        auditLog.ReadRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AssistantSpawnAuditEntry>>(
        [
            new AssistantSpawnAuditEntry(
                DateTimeOffset.Now, AssistantSpawnAction.Stop, SpawnCaller.Assistant, CallerPaneId: null,
                WorkspaceId: "workspace-1", WorkspaceName: "Review", Profile: "claude-sonnet",
                WorkingDirectory: @"C:\repo", PaneId: "pane-1", SessionName: "AC-223", Refusal: null),
            new AssistantSpawnAuditEntry(
                DateTimeOffset.Now.AddMinutes(-5), AssistantSpawnAction.Start, SpawnCaller.Assistant, CallerPaneId: null,
                WorkspaceId: "workspace-2", WorkspaceName: null, Profile: null, WorkingDirectory: null,
                PaneId: null, SessionName: null, Refusal: "The named workspace is not a Sessions desk."),
        ]));
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>(), auditLog);

        await vm.LoadSpawnLogCommand.ExecuteAsync(null);

        Assert.True(vm.HasSpawnLogEntries);
        Assert.Equal(2, vm.SpawnLogEntries.Count);
        Assert.Equal("Stopped", vm.SpawnLogEntries[0].What);
        Assert.Equal("Review", vm.SpawnLogEntries[0].Where);

        // Criterion 5 lists four things by name, and the caller is the first of them — the field that will tell an
        // assistant's spawn from a coordinator's (AC-436) once both write here. A stop names the session instead of
        // a profile and a folder: it started nothing, and printing the profile's default folder under it would be a
        // claim about an action that never took place.
        Assert.Equal("assistant", vm.SpawnLogEntries[0].Who);
        Assert.Equal("AC-223", vm.SpawnLogEntries[0].Session);
        Assert.False(vm.SpawnLogEntries[0].HasStartDetails);

        // The refusal, and the id-fallback for a workspace that was never named.
        Assert.Equal("Start refused", vm.SpawnLogEntries[1].What);
        Assert.Equal("workspace-2", vm.SpawnLogEntries[1].Where);
        Assert.True(vm.SpawnLogEntries[1].HasRefusal);
        Assert.Equal("The named workspace is not a Sessions desk.", vm.SpawnLogEntries[1].Refusal);

        // A refused start produced no session to name, and the row says nothing rather than showing a blank line.
        Assert.False(vm.SpawnLogEntries[1].HasSession);
    }

    [Fact]
    public async Task AStartRowCarriesTheProfileAndTheFolder_TheTwoFieldsThatSayWhatItCosts()
    {
        var auditLog = Substitute.For<IAssistantSpawnAuditLog>();
        auditLog.ReadRecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<AssistantSpawnAuditEntry>>(
        [
            new AssistantSpawnAuditEntry(
                DateTimeOffset.Now, AssistantSpawnAction.Start, SpawnCaller.Assistant, CallerPaneId: null,
                WorkspaceId: "workspace-1", WorkspaceName: "Release", Profile: "claude-opus",
                WorkingDirectory: @"C:\repo", PaneId: "pane-9", SessionName: "AC-545 tests", Refusal: null),
        ]));
        var vm = new AssistantChatViewModel(FakeHost(), FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>(), auditLog);

        await vm.LoadSpawnLogCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.SpawnLogEntries);
        Assert.True(row.HasStartDetails);
        Assert.Contains("claude-opus", row.StartDetails);
        Assert.Contains(@"C:\repo", row.StartDetails);
        Assert.Equal("AC-545 tests", row.Session);
    }

    // AC-545 criterion 6: consent is visual, and only visual. What follows is the negative half of that — the
    // thing that must *not* work — because a spoken "yes, go ahead" is the single most natural way for an
    // operator to try to approve something in a window they are talking to.

    [Fact]
    public async Task SayingYesInTheChat_GrantsNothing_ThePermissionIsStillPendingAfterwards()
    {
        // This covers the spoken path too, and not by analogy: a finished transcript goes to
        // IAssistantSessionHost.SendAsync (AssistantPushToTalkCoordinator, OpenMicCoordinator) — the very method
        // this window's Send command calls. The fake host ends where the real AssistantSessionHost.SendAsync ends,
        // at session.InjectAndSubmit(text), so the words genuinely travel into a live session with a live driver
        // rather than into a substitute that could not have resolved anything anyway.
        var (session, driver) = await _StartedSessionAsync();
        session.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        session.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        var pending = session.Transcript.Single(entry => entry.ToolUseId == "toolu_1");
        Assert.True(pending.IsPendingPermission);

        var host = FakeHost(session);
        host.When(fake => fake.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => session.InjectAndSubmit(call.Arg<string>()));
        var vm = new AssistantChatViewModel(host, FakeSettingsStore(), Substitute.For<IVoicePlaybackQueue>());

        vm.InputText = "yes, allow it";
        await vm.SendCommand.ExecuteAsync(null);

        // The words arrived — as an ordinary message in the conversation, which is all they can ever be.
        await driver.Received(1).SendUserMessageAsync(
            "yes, allow it", Arg.Any<IReadOnlyList<ImageAttachment>>(), Arg.Any<CancellationToken>());

        // And the permission is exactly where it was: nothing was answered, on the wire or in the row.
        await driver.DidNotReceive().RespondToPermissionAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.True(pending.IsPendingPermission);
        Assert.True(session.HasPendingPermission);
        Assert.True(string.IsNullOrEmpty(pending.PermissionDecision));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task OnlyTheAllowButtonOnTheRowResolvesThePermission()
    {
        // The other half: the row's own command is what answers, and it answers the driver rather than merely
        // greying the buttons out. Without this the test above would also pass on a window where nothing at all
        // could grant permission — including the click.
        var (session, driver) = await _StartedSessionAsync();
        session.Apply(new ToolUseRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        session.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "toolu_1", ToolName = "start_agent", InputJson = "{}" });
        var pending = session.Transcript.Single(entry => entry.ToolUseId == "toolu_1");

        await session.AllowToolCommand.ExecuteAsync(pending);

        await driver.Received(1).RespondToPermissionAsync("toolu_1", true, Arg.Any<CancellationToken>());
        Assert.False(pending.IsPendingPermission);
        Assert.False(session.HasPendingPermission);

        await session.DisposeAsync();
    }

    /// <summary>
    /// A live assistant session over a fake driver — the same shape <c>SessionViewModelTests.StartedVm</c> builds,
    /// needed here because the permission machinery only runs against a started runtime.
    /// </summary>
    private static async Task<(SessionViewModel Session, ISessionDriver Driver)> _StartedSessionAsync()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_NoEvents());
        var session = new SessionViewModel(new SessionManager(_FactoryFor(driver)));
        await session.StartConfiguredAsync(
            new SessionProfile("assistant", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort);
        return (session, driver);
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }
}
