using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="OpenMicCoordinator"/>'s routing logic: a finished utterance is cleaned only for SDK
/// sessions (TTY gets the raw text, the same split push-to-talk makes), nothing happens without a
/// selected session, and read-aloud playback pauses/resumes the mic for barge-in. The UI-thread seam
/// <c>InjectUtteranceAsync</c> is driven directly, as with the push-to-talk coordinator.
/// </summary>
public class OpenMicCoordinatorTests
{
    [Fact]
    public async Task InjectUtteranceAsync_SdkSession_CleansThenInjects()
    {
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.CleanupAsync("open the file", Arg.Any<CancellationToken>()).Returns("Open the file.");
        var coordinator = _CreateCoordinator(_CreateSdkSession(), cleanup, out _, out _);

        await coordinator.InjectUtteranceAsync("open the file");

        await cleanup.Received(1).CleanupAsync("open the file", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InjectUtteranceAsync_TtySession_InjectsRawWithoutCleanup()
    {
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        var coordinator = _CreateCoordinator(_CreateTtySession(), cleanup, out _, out _);

        await coordinator.InjectUtteranceAsync("open the file");

        await cleanup.DidNotReceiveWithAnyArgs().CleanupAsync(default!, default);
    }

    [Fact]
    public async Task InjectUtteranceAsync_NoSelectedSession_DoesNothing()
    {
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        var coordinator = _CreateCoordinator(session: null, cleanup, out _, out _);

        await coordinator.InjectUtteranceAsync("open the file");

        await cleanup.DidNotReceiveWithAnyArgs().CleanupAsync(default!, default);
    }

    [Fact]
    public async Task StartAsync_OpenMicEnabled_PausesTheMicWhileReadAloudPlays()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out var listener, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });

        await coordinator.StartAsync();
        playbackQueue.PlaybackActiveChanged += Raise.Event<EventHandler<bool>>(playbackQueue, true);
        playbackQueue.PlaybackActiveChanged += Raise.Event<EventHandler<bool>>(playbackQueue, false);

        await listener.Received(1).StartAsync(Arg.Any<CancellationToken>());
        listener.Received(1).Pause();
        listener.Received(1).Resume();
    }

    [Fact]
    public async Task StartAsync_OpenMicDisabled_NeverStartsTheListener()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });

        await coordinator.StartAsync();

        await listener.DidNotReceiveWithAnyArgs().StartAsync(default);
    }

    [Fact]
    public async Task ToggleOpenMic_StartsThenStopsTheListenerAtRuntime()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });
        await coordinator.StartAsync();

        await coordinator.ToggleOpenMicCommand.ExecuteAsync(null);
        coordinator.IsListening.Should().BeTrue();

        await coordinator.ToggleOpenMicCommand.ExecuteAsync(null);
        coordinator.IsListening.Should().BeFalse();

        await listener.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await listener.Received(1).StopAsync();
    }

    [Fact]
    public async Task ToggleOpenMic_IsDisabledWhenVoiceIsOff()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out _,
            new VoiceSettings { IsEnabled = false });
        await coordinator.StartAsync();

        coordinator.IsAvailable.Should().BeFalse();
        coordinator.ToggleOpenMicCommand.CanExecute(null).Should().BeFalse();
    }

    /// <summary>
    /// Open-mic listens the whole time it is on, so the pill has to appear when the VAD hears speech start —
    /// not when the feature is switched on, and not when the transcript lands, by which time the speaking is
    /// over. Before this it never appeared at all: dictating with open-mic was completely invisible.
    /// </summary>
    [Fact]
    public async Task WhenTheVadHearsSpeechStart_ThePillAppears()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, overlayCoordinator);
        await coordinator.StartAsync();

        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Hidden, "listening to silence is not worth a pill");

        coordinator.HandleSpeechStarted();
        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Listening);

        coordinator.HandleSpeechEnded();
        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Transcribing);
    }

    /// <summary>The pill is released once the text lands, not when the speaking stopped — the cleanup pass runs in between.</summary>
    [Fact]
    public async Task OnceTheUtteranceIsInjected_ThePillGoesAway()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out _,
            overlay: overlayCoordinator);
        await coordinator.StartAsync();
        coordinator.HandleSpeechStarted();
        coordinator.HandleSpeechEnded();

        await coordinator.InjectUtteranceAsync("open the file");

        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Hidden);
    }

    /// <summary>An utterance that cannot be cleaned up or injected still ends. The alternative is a spinner over a sentence that is never coming.</summary>
    [Fact]
    public async Task WhenInjectingThrows_ThePillStillGoesAway()
    {
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.CleanupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new InvalidOperationException("the cleanup model is gone"));
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(_CreateSdkSession(), cleanup, out _, out _, overlay: overlayCoordinator);
        await coordinator.StartAsync();
        coordinator.HandleSpeechStarted();

        var act = async () => await coordinator.InjectUtteranceAsync("open the file");

        await act.Should().ThrowAsync<InvalidOperationException>();
        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Hidden);
    }

    /// <summary>Read-aloud pauses the mic; the pill is how you see why it went quiet rather than wondering.</summary>
    [Fact]
    public async Task WhenReadAloudPlays_ThePillSaysSo()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, overlayCoordinator);
        await coordinator.StartAsync();

        coordinator.HandlePlaybackActiveChanged(true);
        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Speaking);

        coordinator.HandlePlaybackActiveChanged(false);
        overlayCoordinator.Overlay.State.Should().Be(VoiceOverlayState.Hidden);
    }

    /// <summary>
    /// AC-9's microphone half: talking over read-aloud stops it. The hold half already worked and always has
    /// (<c>SessionPanelViewModel.BeginVoiceHold</c>) — a held key needs no threshold, because a room does not
    /// press one by accident. This is the half that has to guess, and it only guesses when asked.
    /// </summary>
    [Fact]
    public async Task TalkingOverReadAloud_StopsIt_WhenTheOperatorAskedForThat()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.4);

        playbackQueue.Received().StopAll();
    }

    [Fact]
    public async Task TheRoomIsNotTalking_SoAQuietMicrophoneLeavesReadAloudAlone()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.05);

        playbackQueue.DidNotReceive().StopAll();
    }

    /// <summary>Off by default: on speakers the microphone hears the read-aloud itself, and a threshold cannot tell that from you.</summary>
    [Fact]
    public async Task WithoutTheSetting_TalkingOverReadAloudDoesNothing()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.9);

        playbackQueue.DidNotReceive().StopAll();
    }

    /// <summary>Nothing is playing, so there is nothing to interrupt — talking is just dictation.</summary>
    [Fact]
    public async Task TalkingWhileNothingIsPlaying_StopsNothing()
    {
        var coordinator = _CreateCoordinator(
            _CreateSdkSession(), Substitute.For<ITranscriptCleanupService>(), out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();

        coordinator.HandleAudioLevel(0.9);

        playbackQueue.DidNotReceive().StopAll();
    }

    /// <summary>A throw here lands on a task nobody observes, leaving a greyed-out toggle and an empty log — the shape of the F9 failure, in the coordinator next door.</summary>
    [Fact]
    public async Task StartAsync_WhenTheSettingsCannotBeRead_LogsIt_RatherThanDyingOnATaskNobodyObserves()
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns<VoiceSettings>(_ => throw new IOException("cockpit.json is being used by another process"));
        var logger = new CapturingLogger<OpenMicCoordinator>();
        var coordinator = _NewCoordinator(new FakeOpenMicListener(), voiceSettingsStore, logger);

        var act = async () => await coordinator.StartAsync();

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    /// <summary>A microphone that will not open leaves the coordinator wired to a listener that is not running — and it would stay wired for the session.</summary>
    [Fact]
    public async Task StartAsync_WhenTheListenerRefusesToStart_LeavesNothingSubscribedToAListenerThatNeverStarted()
    {
        var listener = new FakeOpenMicListener { StartFailure = new InvalidOperationException("the microphone is held by another application") };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        var logger = new CapturingLogger<OpenMicCoordinator>();
        var coordinator = _NewCoordinator(listener, voiceSettingsStore, logger);

        await coordinator.StartAsync();

        listener.UtteranceSubscriberCount.Should().Be(0);
        coordinator.IsListening.Should().BeFalse();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
    }

    /// <summary>Voice is on even when open-mic will not start: the toggle is what the operator retries with, and a failed start must not disable it.</summary>
    [Fact]
    public async Task StartAsync_WhenTheListenerRefusesToStart_LeavesTheToggleAvailableToTryAgain()
    {
        var listener = new FakeOpenMicListener { StartFailure = new InvalidOperationException("the microphone is held by another application") };
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        var coordinator = _NewCoordinator(listener, voiceSettingsStore, new CapturingLogger<OpenMicCoordinator>());

        await coordinator.StartAsync();

        coordinator.IsAvailable.Should().BeTrue();
        coordinator.ToggleOpenMicCommand.CanExecute(null).Should().BeTrue();
    }

    private static OpenMicCoordinator _NewCoordinator(
        IOpenMicListener listener,
        IVoiceSettingsStore voiceSettingsStore,
        ILogger<OpenMicCoordinator> logger) =>
        new(listener,
            TestCockpit.NewViewModel(),
            voiceSettingsStore,
            Substitute.For<ITranscriptCleanupService>(),
            Substitute.For<IVoicePlaybackQueue>(),
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            logger);

    /// <param name="overlay">Pass one to assert on the pill; omit it and the coordinator reports into a throwaway.</param>
    private static OpenMicCoordinator _CreateCoordinator(
        SessionPanelViewModel? session,
        ITranscriptCleanupService cleanup,
        out IOpenMicListener listener,
        out IVoicePlaybackQueue playbackQueue,
        VoiceSettings? settings = null,
        VoiceOverlayCoordinator? overlay = null)
    {
        listener = Substitute.For<IOpenMicListener>();
        playbackQueue = Substitute.For<IVoicePlaybackQueue>();
        var cockpit = TestCockpit.NewViewModel();
        cockpit.SelectedSession = session;
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? new VoiceSettings());
        return new OpenMicCoordinator(
            listener, cockpit, voiceSettingsStore, cleanup, playbackQueue,
            overlay ?? new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            NullLogger<OpenMicCoordinator>.Instance);
    }

    private static SessionPanelViewModel _CreateSdkSession()
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voiceSettingsStore: voiceSettingsStore);
    }

    private static SessionPanelViewModel _CreateTtySession()
    {
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        return new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>(), voiceSettingsStore: voiceSettingsStore);
    }
}
