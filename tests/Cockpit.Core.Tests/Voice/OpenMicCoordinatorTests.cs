using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;
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

    private static OpenMicCoordinator _CreateCoordinator(
        SessionPanelViewModel? session,
        ITranscriptCleanupService cleanup,
        out IOpenMicListener listener,
        out IVoicePlaybackQueue playbackQueue,
        VoiceSettings? settings = null)
    {
        listener = Substitute.For<IOpenMicListener>();
        playbackQueue = Substitute.For<IVoicePlaybackQueue>();
        var cockpit = TestCockpit.NewViewModel();
        cockpit.SelectedSession = session;
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? new VoiceSettings());
        return new OpenMicCoordinator(listener, cockpit, voiceSettingsStore, cleanup, playbackQueue);
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
        return new ClaudeTtyViewModel(Substitute.For<ITtyLauncher>(), Substitute.For<ITtySessionProvider>(), voiceSettingsStore: voiceSettingsStore);
    }
}
