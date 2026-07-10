using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.SessionSwitching;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using FluentAssertions;
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

    [Fact]
    public void HandleHoldStarted_NoSelectedSession_StillShowsTheOverlay_AndDoesNotThrow()
    {
        var overlayPresenter = new FakeVoiceOverlayPresenter();
        var coordinator = _CreateCoordinator(session: null, overlayPresenter, out var overlay);

        var act = coordinator.HandleHoldStarted;

        act.Should().NotThrow();
        overlay.State.Should().Be(VoiceOverlayState.Listening);
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

        await coordinator.HandleHoldEndedAsync();

        await voicePushToTalk.Received(1).EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>());
        overlay.State.Should().Be(VoiceOverlayState.Hidden);
        overlayPresenter.HideCallCount.Should().Be(1);
    }

    [Fact]
    public async Task HandleHoldEndedAsync_TtySession_EndsTheHoldWithoutCleanup()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>()).Returns("open the file");
        var session = _CreateTtySession(voicePushToTalk);
        var coordinator = _CreateCoordinator(session, new FakeVoiceOverlayPresenter(), out _);

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
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter(),
            Substitute.For<IVoicePushToTalkService>());

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
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, overlay, overlayPresenter,
            Substitute.For<IVoicePushToTalkService>());

        await coordinator.StartAsync();
        hotkeyService.RaiseHoldStarted();

        hotkeyService.WasStarted.Should().BeTrue();
    }

    private static SessionPanelViewModel _CreateSdkSession(IVoicePushToTalkService voicePushToTalk)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new ClaudeSessionViewModel(Substitute.For<ISessionDriverFactory>(), voicePushToTalk, voiceSettingsStore);
    }

    private static SessionPanelViewModel _CreateTtySession(IVoicePushToTalkService voicePushToTalk)
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>(), voicePushToTalk, voiceSettingsStore);
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
            new FakeGlobalHotkeyService(), cockpit, voiceSettingsStore, overlay, overlayPresenter, Substitute.For<IVoicePushToTalkService>());
    }

    private static CockpitViewModel NewCockpitViewModel()
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var sessionSwitchSettingsStore = Substitute.For<ISessionSwitchSettingsStore>();
        sessionSwitchSettingsStore.LoadAsync().Returns(new SessionSwitchSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            sessionSwitchSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore);
    }
}
