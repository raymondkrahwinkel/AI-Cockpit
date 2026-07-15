using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Sessions;
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
            hotkeyService, NewCockpitViewModel(), voiceSettingsStore, overlay, overlayPresenter,
            Substitute.For<IVoicePushToTalkService>(), NullLogger<VoicePushToTalkCoordinator>.Instance);

        await coordinator.StartAsync();
        hotkeyService.RaiseHoldStarted();

        hotkeyService.WasStarted.Should().BeTrue();
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
            new FakeGlobalHotkeyService(), cockpit, voiceSettingsStore, overlay, overlayPresenter, Substitute.For<IVoicePushToTalkService>(),
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
            () => new ClaudeTtyViewModel(),
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
