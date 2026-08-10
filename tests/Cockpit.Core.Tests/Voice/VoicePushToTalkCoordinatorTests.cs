using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Tests.Hotkeys;
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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="VoicePushToTalkCoordinator"/>'s routing/overlay-state logic (#34) — the UI-thread half of
/// <c>HandleHoldStarted</c>/<c>HandleHoldEndedAsync</c> the real event handlers marshal onto via
/// <c>Dispatcher.UIThread.Post</c>. Driving a real Avalonia dispatcher loop from a unit test is not
/// practical, so these tests call the internal test seam directly (see the class remarks) — everything
/// past the dispatcher hop is exercised for real: the selected session's actual
/// <see cref="IVoicePushToTalkService"/>, its own report into the pill, and a fake overlay presenter.
/// </summary>
public class VoicePushToTalkCoordinatorTests
{
    [Fact]
    public void HandleHoldStarted_ShowsTheOverlayListening_AndBeginsAHoldOnTheSelectedSession()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        var coordinator = _CreateCoordinator(session, pill);

        coordinator.HandleHoldStarted();

        Assert.Equal(VoiceOverlayState.Listening, pill.Overlay.State);
        Assert.Equal(1, pill.Presenter.ShowCallCount);
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
        var pill = _NewPill();
        var coordinator = _CreateCoordinator(session: null, pill);

        var act = coordinator.HandleHoldStarted;

        act();
        Assert.Equal(VoiceOverlayState.Unavailable, pill.Overlay.State);
        Assert.Equal("No session selected", pill.Overlay.StatusText);
        Assert.False(pill.Overlay.IsListening);
        Assert.Equal(1, pill.Presenter.ShowCallCount);
    }

    [Fact]
    public async Task HandleHoldEndedAsync_SdkSession_EndsTheHold_AndHidesTheOverlay()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the file");
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        await voicePushToTalk.Received(1).EndHoldAsync(Arg.Any<CancellationToken>());
        Assert.Equal(VoiceOverlayState.Hidden, pill.Overlay.State);
        Assert.Equal(1, pill.Presenter.HideCallCount);
    }

    /// <summary>
    /// AC-557, the reported defect: a long dictation that produced nothing hid the pill regardless and said
    /// nothing anywhere. The reason now stays on screen — the hold's own report, which the coordinator no longer
    /// paints over.
    /// </summary>
    [Fact]
    public async Task HandleHoldEndedAsync_WhenNothingWasTranscribed_LeavesTheReasonOnThePill()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(VoiceOverlayState.Failed, pill.Overlay.State);
        Assert.NotEmpty(pill.Overlay.StatusText);
        Assert.Equal(0, pill.Presenter.HideCallCount);
    }

    /// <summary>The other way a dictation ends in nothing: the transcriber threw, and only the log knew.</summary>
    [Fact]
    public async Task HandleHoldEndedAsync_WhenTranscriptionThrows_SaysSoOnThePill()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("the speech model could not be loaded"));
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(VoiceOverlayState.Failed, pill.Overlay.State);
        Assert.Contains("the speech model could not be loaded", pill.Overlay.StatusText);
    }

    /// <summary>A session whose voice is switched off declines the hold just as silently — and just as invisibly.</summary>
    [Fact]
    public void HandleHoldStarted_WhenTheSessionHasVoiceOff_SaysSoInsteadOfClaimingToListen()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(false);
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        session.VoiceEnabled = false;
        var coordinator = _CreateCoordinator(session, pill);

        coordinator.HandleHoldStarted();

        Assert.Equal(VoiceOverlayState.Unavailable, pill.Overlay.State);
        Assert.Equal("Voice is off for this session", pill.Overlay.StatusText);
    }

    /// <summary>
    /// This used to assert the opposite: open-mic won and the hold stood down as a duplicate. AC-627 reversed it,
    /// because the two do not send to the same place — standing down swapped the recipient and skipped the review.
    /// </summary>
    [Fact]
    public void HandleHoldStarted_WhenOpenMicIsListening_TakesTheMicrophoneOffIt_RatherThanStandingDown()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        var openMic = _ListeningOpenMic(out var suspension);
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, openMic, pill);
        var coordinator = _CreateCoordinator(session, pill);

        coordinator.HandleHoldStarted();

        Assert.Equal(VoiceOverlayState.Listening, pill.Overlay.State);
        voicePushToTalk.Received(1).BeginHold();
        openMic.Received(1).SuspendForHold();
        suspension.DidNotReceive().Dispose();
    }

    /// <summary>
    /// Criterion 2: the microphone comes back on release. One the operator has to switch on again after every
    /// dictation is not "Always On".
    /// </summary>
    [Fact]
    public async Task WhenTheHoldEnds_OpenMicGetsTheMicrophoneBack_WithoutTheOperatorDoingAnything()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        var openMic = _ListeningOpenMic(out var suspension);
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, openMic, pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        suspension.Received(1).Dispose();
    }

    /// <param name="suspension">The handle the hold holds until it ends — asserted on to tell "paused" from "paused and released".</param>
    private static IOpenMicState _ListeningOpenMic(out IDisposable suspension)
    {
        var openMic = Substitute.For<IOpenMicState>();
        openMic.IsListening.Returns(true);
        suspension = Substitute.For<IDisposable>();
        openMic.SuspendForHold().Returns(suspension);
        return openMic;
    }

    /// <summary>
    /// Nothing was captured, so there is nothing to transcribe — flashing "Transcribing…" over an empty
    /// recording is the same lie in another word.
    /// </summary>
    [Fact]
    public async Task HandleHoldEndedAsync_WhenNothingWasRecorded_NeverClaimsToTranscribe()
    {
        var pill = _NewPill();
        var coordinator = _CreateCoordinator(session: null, pill);
        coordinator.HandleHoldStarted();

        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(VoiceOverlayState.Hidden, pill.Overlay.State);
        Assert.False(pill.Overlay.IsTranscribing);
        Assert.Equal(1, pill.Presenter.HideCallCount);
    }

    [Fact]
    public async Task HandleHoldEndedAsync_TtySession_EndsTheHold()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the file");
        var pill = _NewPill();
        var session = _CreateTtySession(voicePushToTalk, pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        await voicePushToTalk.Received(1).EndHoldAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Criterion 2's other half: a terminal session says it too. The pill is one window for both kinds, and it is
    /// the session — not the route — that reports into it, so there is one sentence rather than two that could
    /// drift apart.
    /// </summary>
    [Fact]
    public async Task HandleHoldEndedAsync_TtySession_WhenNothingWasTranscribed_SaysSoOnThePill()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns(string.Empty);
        var pill = _NewPill();
        var session = _CreateTtySession(voicePushToTalk, pill);
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);

        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(VoiceOverlayState.Failed, pill.Overlay.State);
        Assert.NotEmpty(pill.Overlay.StatusText);
    }

    [Fact]
    public async Task HandleHoldEndedAsync_SetsTranscribingBeforeHiding()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the file");
        var pill = _NewPill();
        var session = _CreateSdkSession(voicePushToTalk, pill: pill);
        var states = new List<VoiceOverlayState>();
        var coordinator = _CreateCoordinator(session, pill);
        _StartARecordingHold(coordinator, session, voicePushToTalk);
        pill.Overlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VoiceOverlayViewModel.State))
            {
                states.Add(pill.Overlay.State);
            }
        };

        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(new[] { VoiceOverlayState.Transcribing, VoiceOverlayState.Hidden }, states);
    }

    /// <summary>
    /// Which key the hold answers to is not always the cockpit's to say: a Wayland compositor binds what it likes
    /// and takes the configured key as a hint, and can rebind it from its own settings at any time. The pill's
    /// settings row reports what it was told rather than the key that was typed.
    /// </summary>
    [Fact]
    public async Task TheTriggerTheDesktopReports_IsWhatTheOperatorIsShown()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        hotkeyService.TriggerDescriptions[GlobalHotkeys.PushToTalk] = "Meta+F9";
        var hotkeys = TestGlobalHotkeys.Coordinator(hotkeyService, TestGlobalHotkeys.GlobalPushToTalkOn);
        var cockpit = NewCockpitViewModel();
        var coordinator = _CreateCoordinatorOnHotkeys(hotkeys, cockpit);
        await hotkeys.ApplyAsync();

        coordinator.HandleTriggerDescriptionsChanged();

        Assert.Equal("Meta+F9", cockpit.VoiceGlobalHotkeyTrigger);
    }

    /// <summary>A desktop that binds nothing leaves the operator with a hotkey that never fires and no way to know why — so it says where to bind it.</summary>
    [Fact]
    public async Task WhenNothingBoundIt_TheOperatorIsToldWhereToDoIt()
    {
        var hotkeys = TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService(), TestGlobalHotkeys.GlobalPushToTalkOn);
        var cockpit = NewCockpitViewModel();
        var coordinator = _CreateCoordinatorOnHotkeys(hotkeys, cockpit);
        await hotkeys.ApplyAsync();

        coordinator.HandleTriggerDescriptionsChanged();

        Assert.NotEmpty(cockpit.VoiceGlobalHotkeyTrigger);
    }

    /// <summary>
    /// Global push-to-talk off means there is nothing to report — not a stale trigger from before it was switched
    /// off, and not the "your desktop has not bound it yet" line, which about a key nobody enabled would send an
    /// operator into their shortcut settings looking for something that was never asked for.
    /// </summary>
    [Fact]
    public async Task WithGlobalPushToTalkOff_ThereIsNothingToReport()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        hotkeyService.TriggerDescriptions[GlobalHotkeys.PushToTalk] = "F9";
        var hotkeys = TestGlobalHotkeys.Coordinator(hotkeyService, new VoiceSettings { IsEnabled = true, GlobalPushToTalk = false });
        var cockpit = NewCockpitViewModel();
        var coordinator = _CreateCoordinatorOnHotkeys(hotkeys, cockpit);
        await hotkeys.ApplyAsync();

        coordinator.HandleTriggerDescriptionsChanged();

        Assert.Empty(cockpit.VoiceGlobalHotkeyTrigger);
    }

    /// <summary>
    /// AC-691: the Options button that asks the desktop for its shortcuts permission again must actually force a
    /// new portal session — not just exist. <see cref="CockpitViewModel.HotkeyPortalRetryRequested"/> is this
    /// coordinator's to re-arm on, the same seam <c>VoiceSettingsSaved</c> uses, so a click re-runs
    /// <see cref="IGlobalHotkeyService.StartAsync"/> (which is what tears down and rebuilds the portal session —
    /// see <c>PortalGlobalHotkeyService.StartAsync</c>) rather than leaving the stale one in place.
    /// </summary>
    [Fact]
    public async Task RequestingAPortalRetry_ReArmsTheHotkeysWithTheOsService()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var hotkeys = TestGlobalHotkeys.Coordinator(hotkeyService, TestGlobalHotkeys.GlobalPushToTalkOn);
        var cockpit = NewCockpitViewModel();
        _CreateCoordinatorOnHotkeys(hotkeys, cockpit);
        await hotkeys.ApplyAsync();
        Assert.Equal(1, hotkeyService.StartCallCount);

        cockpit.RetryHotkeyPortalPermissionCommand.Execute(null);

        Assert.Equal(2, hotkeyService.StartCallCount);
    }

    /// <summary>A press of another feature's key reaches this coordinator too — it must ignore anything that is not its own.</summary>
    [Fact]
    public async Task APressOfAnotherFeaturesKey_StartsNoHold()
    {
        var hotkeyService = new FakeGlobalHotkeyService();
        var hotkeys = TestGlobalHotkeys.Coordinator(hotkeyService, TestGlobalHotkeys.GlobalPushToTalkOn);
        var pushToTalk = Substitute.For<IVoicePushToTalkService>();
        var cockpit = NewCockpitViewModel();
        cockpit.SelectedSession = _CreateSdkSession(pushToTalk);
        _CreateCoordinatorOnHotkeys(hotkeys, cockpit, pushToTalk);
        await hotkeys.ApplyAsync();

        hotkeyService.RaisePressed(GlobalHotkeys.Screenshot);

        pushToTalk.DidNotReceive().BeginHold();
    }

    private static VoicePushToTalkCoordinator _CreateCoordinatorOnHotkeys(
        GlobalHotkeyCoordinator hotkeys,
        CockpitViewModel cockpit,
        IVoicePushToTalkService? pushToTalk = null,
        Pill? pill = null) =>
        new(hotkeys,
            cockpit,
            (pill ?? _NewPill()).Coordinator,
            pushToTalk ?? Substitute.For<IVoicePushToTalkService>(),
            NullLogger<VoicePushToTalkCoordinator>.Instance);

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

        Assert.Equal(1, pushToTalk.AudioLevelSubscriberCount);
    }

    /// <summary>The ordinary hold still leaves nothing behind — the detach must not cost the release its own.</summary>
    [Fact]
    public async Task AHoldThatStartsAndEnds_LeavesNothingOnTheLevelFeed()
    {
        var pushToTalk = new FakeVoicePushToTalkService();
        var coordinator = _CreateCoordinatorOn(pushToTalk);

        coordinator.HandleHoldStarted();
        await coordinator.HandleHoldEndedAsync();

        Assert.Equal(0, pushToTalk.AudioLevelSubscriberCount);
    }

    /// <param name="pushToTalk">Given to both the coordinator and the selected session — one shared service reaches both in the real graph.</param>
    private static VoicePushToTalkCoordinator _CreateCoordinatorOn(IVoicePushToTalkService pushToTalk)
    {
        var cockpit = NewCockpitViewModel();
        var pill = _NewPill();
        cockpit.SelectedSession = _CreateSdkSession(pushToTalk, pill: pill);

        return _CreateCoordinatorOnHotkeys(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()), cockpit, pushToTalk, pill);
    }

    /// <summary>
    /// The overlay half of the graph, built before the session because the session reports its own hold into it —
    /// which is what makes the in-window F9 route say the same things this one does (AC-557).
    /// </summary>
    internal sealed record Pill(
        VoiceOverlayCoordinator Coordinator, VoiceOverlayViewModel Overlay, FakeVoiceOverlayPresenter Presenter);

    internal static Pill _NewPill()
    {
        var overlay = new VoiceOverlayViewModel();
        var presenter = new FakeVoiceOverlayPresenter();
        return new Pill(new VoiceOverlayCoordinator(overlay, presenter), overlay, presenter);
    }

    private static SessionPanelViewModel _CreateSdkSession(
        IVoicePushToTalkService voicePushToTalk, IOpenMicState? openMicState = null, Pill? pill = null)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore,
            openMicState: openMicState, voiceOverlay: pill?.Coordinator);
    }

    private static SessionPanelViewModel _CreateTtySession(IVoicePushToTalkService voicePushToTalk, Pill? pill = null)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new TtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePushToTalk, voiceSettingsStore,
            voiceOverlay: pill?.Coordinator);
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

    private static VoicePushToTalkCoordinator _CreateCoordinator(SessionPanelViewModel? session, Pill pill)
    {
        var cockpit = NewCockpitViewModel();
        cockpit.SelectedSession = session;
        return new VoicePushToTalkCoordinator(
            TestGlobalHotkeys.Coordinator(new FakeGlobalHotkeyService()),
            cockpit,
            pill.Coordinator,
            Substitute.For<IVoicePushToTalkService>(),
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
