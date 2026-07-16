using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="VoicePushToTalkCoordinator"/>'s routing/overlay-state logic (#34) — the UI-thread half of
/// <c>HandleHoldStarted</c>/<c>HandleHoldEndedAsync</c> the real event handlers marshal onto via
/// <c>Dispatcher.UIThread.Post</c>. Driving a real Avalonia dispatcher loop from a unit test is not
/// practical, so these tests call the internal test seam directly (see the class remarks) — everything
/// past the dispatcher hop is exercised for real: the selected session's actual
/// <see cref="IVoicePushToTalkService"/> and a fake overlay presenter.
/// </summary>
public class VoicePushToTalkCoordinatorTests
{
    [Fact]
    public void HandleHoldStarted_ShowsTheOverlayListening_AndBeginsAHoldOnTheSelectedSession()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        var session = _CreateSdkSession(voicePushToTalk);
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = _CreateCoordinator(session, overlayPresenter, out var overlay);

        coordinator.HandleHoldStarted();

        overlay.State.Should().Be(VoiceOverlayState.Listening);
        overlayPresenter.ShowCallCount.Should().Be(1);
        voicePushToTalk.Received(1).BeginHold();
    }

    /// <summary>
    /// Raymond, holding the key over a cockpit with nothing open: the pill said "Listening" over a waveform
    /// that never moved, because there was no session to route a microphone to. This test asserted exactly
    /// that — it was green because it encoded the bug. The overlay still shows and still must not throw; what
    /// it says had to change.
    /// </summary>
    [Fact]
    public void HandleHoldStarted_NoSelectedSession_ShowsTheOverlaySayingSo_AndDoesNotThrow()
    {
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = _CreateCoordinator(session: null, overlayPresenter, out var overlay);

        var act = coordinator.HandleHoldStarted;

        act.Should().NotThrow();
        overlay.State.Should().Be(VoiceOverlayState.Unavailable);
        overlay.StatusText.Should().Be("No session selected");
        overlay.IsListening.Should().BeFalse();
        overlayPresenter.ShowCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHoldEndedAsync_SdkSession_EndsTheHoldWithCleanup_AndHidesTheOverlay()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>()).Returns("open the file");
        var session = _CreateSdkSession(voicePushToTalk);
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = _CreateCoordinator(session, overlayPresenter, out var overlay);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        await voicePushToTalk.Received(1).EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>());
        overlay.State.Should().Be(VoiceOverlayState.Hidden);
        overlayPresenter.HideCallCount.Should().Be(1);
    }

    /// <summary>A session whose voice is switched off declines the hold just as silently — and just as invisibly.</summary>
    [Fact]
    public void HandleHoldStarted_WhenTheSessionHasVoiceOff_SaysSoInsteadOfClaimingToListen()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(false);
        var session = _CreateSdkSession(voicePushToTalk);
        session.VoiceEnabled = false;
        var coordinator = _CreateCoordinator(session, new FakeVoiceOverlayPresenter(), out var overlay);

        coordinator.HandleHoldStarted();

        overlay.State.Should().Be(VoiceOverlayState.Unavailable);
        overlay.StatusText.Should().Be("Voice is off for this session");
    }

    /// <summary>
    /// Nothing was captured, so there is nothing to transcribe — flashing "Transcribing…" over an empty
    /// recording is the same lie in another word.
    /// </summary>
    [Fact]
    public async Task HandleHoldEndedAsync_WhenNothingWasRecorded_NeverClaimsToTranscribe()
    {
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = _CreateCoordinator(session: null, overlayPresenter, out var overlay);
        coordinator.HandleHoldStarted();

        await coordinator.HandleHoldEndedAsync();

        overlay.State.Should().Be(VoiceOverlayState.Hidden);
        overlay.IsTranscribing.Should().BeFalse();
        overlayPresenter.HideCallCount.Should().Be(1);
    }

    /// <summary>
    /// First use downloads the model and a GPU runtime inside the hold, and the pill spent that time on a
    /// spinner reading "Transcribing…" — for minutes, while nothing was being transcribed. Each step both
    /// names what is being waited on and claims the pill's state, because on every later run there is nothing
    /// to prepare and the pill should go straight to the spinner.
    /// </summary>
    [Fact]
    public void HandlePreparing_ShowsTheDownloadInsteadOfAFalseTranscribingSpinner()
    {
        var coordinator = _CreateCoordinator(session: null, new FakeVoiceOverlayPresenter(), out var overlay);
        overlay.State = VoiceOverlayState.Transcribing;

        coordinator.HandlePreparing(new VoicePreparationProgress("Downloading Vulkan runtime — 43% of 151 MB", 0.43));

        overlay.State.Should().Be(VoiceOverlayState.Preparing);
        overlay.StatusText.Should().Be("Downloading Vulkan runtime — 43% of 151 MB");
        overlay.HasProgress.Should().BeTrue();
        overlay.ProgressValue.Should().Be(0.43);
    }

    /// <summary>A step with nothing to measure against passes its missing fraction through, so the bar hides rather than invent one.</summary>
    [Fact]
    public void HandlePreparing_WithoutAFraction_LeavesTheOverlayWithNoBar()
    {
        var coordinator = _CreateCoordinator(session: null, new FakeVoiceOverlayPresenter(), out var overlay);

        coordinator.HandlePreparing(new VoicePreparationProgress("Downloading speech model — 412 MB"));

        overlay.State.Should().Be(VoiceOverlayState.Preparing);
        overlay.HasProgress.Should().BeFalse();
    }

    /// <summary>
    /// Without this the last download line would sit on the pill through the transcription itself, which moves
    /// the lie one step along instead of ending it.
    /// </summary>
    [Fact]
    public void HandlePrepared_HandsThePillBackToTheSpinnerThatIsNowTrue()
    {
        var coordinator = _CreateCoordinator(session: null, new FakeVoiceOverlayPresenter(), out var overlay);
        coordinator.HandlePreparing(new VoicePreparationProgress("Loading speech model…"));

        coordinator.HandlePrepared();

        overlay.State.Should().Be(VoiceOverlayState.Transcribing);
        overlay.StatusText.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleHoldEndedAsync_TtySession_EndsTheHoldWithoutCleanup()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>()).Returns("open the file");
        var session = _CreateTtySession(voicePushToTalk);
        var coordinator = _CreateCoordinator(session, new FakeVoiceOverlayPresenter(), out _);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        await voicePushToTalk.Received(1).EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleHoldEndedAsync_SetsTranscribingBeforeHiding()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        var session = _CreateSdkSession(voicePushToTalk);
        var states = new List<VoiceOverlayState>();
        var coordinator = _CreateCoordinator(session, new FakeVoiceOverlayPresenter(), out var overlay);
        _StartARecordingHold(coordinator, session, voicePushToTalk);
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceOverlayViewModel.State))
            {
                states.Add(overlay.State);
            }
        };

        await coordinator.HandleHoldEndedAsync();

        states.Should().Equal(VoiceOverlayState.Transcribing, VoiceOverlayState.Hidden);
    }

    [Fact]
    public async Task StartAsync_GlobalPushToTalkDisabled_NeverStartsTheHotkeyService()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = false });
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();

        hotkeyService.WasStarted.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_VoiceAndGlobalPushToTalkEnabled_StartsTheHotkeyServiceAndRoutesHolds()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var overlay = new VoiceOverlayViewModel();
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, new VoiceOverlayCoordinator(overlay, overlayPresenter),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();
        hotkeyService.RaiseHoldStarted();

        hotkeyService.WasStarted.Should().BeTrue();
    }

    /// <summary>
    /// Its caller discards the task (<c>App.axaml.cs</c>: <c>_ = …StartAsync()</c>), so a throw here used to land
    /// on a task nobody observes and take the hotkey with it. On 2026-07-15 that happened for real: reading the
    /// voice settings hit <c>cockpit.json</c> while the plugin layer was writing it, and F9 was dead for the whole
    /// session with not one line in the log. It still cannot arm — but it has to say so.
    /// </summary>
    [Fact]
    public async Task StartAsync_WhenTheSettingsCannotBeRead_LogsIt_RatherThanDyingOnATaskNobodyObserves()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns<VoiceSettings>(_ => throw new IOException("cockpit.json is being used by another process"));
        var logger = new CapturingLogger<VoicePushToTalkCoordinator>();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), logger);

        var act = async () => await coordinator.StartAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    [Fact]
    public async Task StartAsync_WhenTheHotkeyServiceRefusesToStart_LeavesNothingSubscribedToAHookThatNeverArmed()
    {
        var hotkeyService = new FakeGlobalHotkeyService { StartFailure = new InvalidOperationException("no hook for you") };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var overlay = new VoiceOverlayViewModel();
        var logger = new CapturingLogger<VoicePushToTalkCoordinator>();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, new VoiceOverlayCoordinator(overlay, new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), logger);

        await coordinator.StartAsync();

        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error);

        // A hook that never armed cannot raise this — but if it somehow did, nothing should still be listening.
        hotkeyService.RaiseHoldStarted();
        overlay.State.Should().Be(VoiceOverlayState.Hidden, "the coordinator unsubscribed when the hook refused");
    }

    /// <summary>
    /// The key was read once, at startup, and nothing re-armed: changing it in Options saved the new key and left
    /// the hook listening for the old one for the rest of the session, with nothing anywhere saying so. Raymond:
    /// "we kunnen de keybind niet aanpassen" — you could type it; it simply did nothing.
    /// </summary>
    [Fact]
    public async Task SavingTheVoiceSettings_ReArmsTheHotkey_RatherThanLeavingItOnTheOldKey()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);
        await coordinator.StartAsync();

        await coordinator.ReapplyAsync();

        hotkeyService.StopCallCount.Should().Be(1, "the old key has to be let go before the new one is armed");
        hotkeyService.StartCallCount.Should().Be(2);
    }

    /// <summary>Re-arming must not double the hold: a second subscription on the same hook means every press fires twice.</summary>
    [Fact]
    public async Task ReArming_DoesNotSubscribeTwice()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);
        await coordinator.StartAsync();

        await coordinator.ReapplyAsync();
        await coordinator.ReapplyAsync();

        hotkeyService.HoldStartedSubscriberCount.Should().Be(1);
    }

    /// <summary>
    /// Which key the hold answers to is not always the cockpit's to say: a Wayland compositor binds what it likes
    /// and takes the configured key as a hint, and can rebind it from its own settings at any time. The pill's
    /// settings row reports what it was told rather than the key that was typed.
    /// </summary>
    [Fact]
    public async Task TheTriggerTheDesktopReports_IsWhatTheOperatorIsShown()
    {
        var hotkeyService = new FakeGlobalHotkeyService { TriggerDescription = "Meta+F9" };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var cockpit = NewCockpitViewModel();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, cockpit, voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();

        cockpit.VoiceGlobalHotkeyTrigger.Should().Be("Meta+F9", "the compositor bound that, whatever the settings asked for");
    }

    /// <summary>A desktop that binds nothing leaves the operator with a hotkey that never fires and no way to know why — so it says where to bind it.</summary>
    [Fact]
    public async Task WhenNothingBoundIt_TheOperatorIsToldWhereToDoIt()
    {
        var hotkeyService = new FakeGlobalHotkeyService { TriggerDescription = null };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true });
        var cockpit = NewCockpitViewModel();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, cockpit, voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();

        cockpit.VoiceGlobalHotkeyTrigger.Should().NotBeEmpty();
    }

    /// <summary>Global push-to-talk off means there is nothing to report — not a stale trigger from before it was switched off.</summary>
    [Fact]
    public async Task WithGlobalPushToTalkOff_ThereIsNothingToReport()
    {
        var hotkeyService = new FakeGlobalHotkeyService { TriggerDescription = "F9" };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, GlobalPushToTalk = false });
        var cockpit = NewCockpitViewModel();
        var coordinator = new VoicePushToTalkCoordinator(
            hotkeyService, cockpit, voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();

        cockpit.VoiceGlobalHotkeyTrigger.Should().BeEmpty();
    }

    /// <summary>
    /// One subscription per hold, whatever arrives. Neither backend repeats a hold today — both gate their
    /// <c>HoldStarted</c> on an <c>_isHolding</c> flag — so the second call here cannot currently reach this
    /// through them. That is a promise two other classes make, and this coordinator's level feed should not stack
    /// if one of them ever stops keeping it. The count is asserted directly because the real handler marshals
    /// through a dispatcher no unit test pumps, which is what makes a doubled subscription invisible.
    /// </summary>
    [Fact]
    public void HandleHoldStarted_TwiceOverWithoutAnEnd_LeavesOneSubscriptionOnTheLevelFeed()
    {
        var pushToTalk = new FakeVoicePushToTalkService();
        var coordinator = _CreateCoordinatorOn(pushToTalk);

        coordinator.HandleHoldStarted();
        coordinator.HandleHoldStarted();

        pushToTalk.AudioLevelSubscriberCount.Should().Be(1);
    }

    /// <summary>The ordinary hold still leaves nothing behind — the detach must not cost the release its own.</summary>
    [Fact]
    public async Task AHoldThatStartsAndEnds_LeavesNothingOnTheLevelFeed()
    {
        var pushToTalk = new FakeVoicePushToTalkService();
        var coordinator = _CreateCoordinatorOn(pushToTalk);

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();

        pushToTalk.AudioLevelSubscriberCount.Should().Be(0);
    }

    /// <param name="pushToTalk">Given to both the coordinator and the selected session — one shared service reaches both in the real graph.</param>
    private static VoicePushToTalkCoordinator _CreateCoordinatorOn(IVoicePushToTalkService pushToTalk)
    {
        var cockpit = NewCockpitViewModel();
        cockpit.SelectedSession = _CreateSdkSession(pushToTalk);
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());

        return new VoicePushToTalkCoordinator(
            new FakeGlobalHotkeyService(),
            cockpit,
            voiceSettingsStore,
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            pushToTalk,
            NullLogger<VoicePushToTalkCoordinator>.Instance);
    }

    private static SessionPanelViewModel _CreateSdkSession(IVoicePushToTalkService voicePushToTalk)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore);
    }

    private static SessionPanelViewModel _CreateTtySession(IVoicePushToTalkService voicePushToTalk)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), voicePushToTalk, voiceSettingsStore);
    }

    /// <summary>Resolves any profile (including none) to a fresh provider substitute — same as the real resolver does for a Claude profile or a profile-less session.</summary>
    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    /// <summary>
    /// Puts a hold that really opened a microphone in progress, which is the only way a hold ever ends. These
    /// tests used to call the end handler with no hold at all — a state the hotkey service cannot produce, and
    /// the reason it went unnoticed that the end path never checked whether anything had been recorded.
    /// </summary>
    private static void _StartARecordingHold(
        VoicePushToTalkCoordinator coordinator, SessionPanelViewModel session, IVoicePushToTalkService voicePushToTalk)
    {
        session.VoiceEnabled = true;
        voicePushToTalk.BeginHold().Returns(true);
        coordinator.HandleHoldStarted();
    }

    private static VoicePushToTalkCoordinator _CreateCoordinator(
        SessionPanelViewModel? session, IVoiceOverlayPresenter overlayPresenter, out VoiceOverlayViewModel overlay)
    {
        var cockpit = NewCockpitViewModel();
        cockpit.SelectedSession = session;
        overlay = new VoiceOverlayViewModel();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());
        return new VoicePushToTalkCoordinator(
            new FakeGlobalHotkeyService(), cockpit, voiceSettingsStore, new VoiceOverlayCoordinator(overlay, overlayPresenter), Substitute.For<IVoicePushToTalkService>(),
            NullLogger<VoicePushToTalkCoordinator>.Instance);
    }

    private static CockpitViewModel NewCockpitViewModel()
    {
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
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);
    }
}
