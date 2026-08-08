using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="OpenMicCoordinator"/>'s routing logic: a finished utterance goes to the assistant, raw, and
/// read-aloud playback pauses/resumes the mic for barge-in. The UI-thread seam <c>InjectUtteranceAsync</c> is
/// driven directly, as with the push-to-talk coordinator.
/// </summary>
/// <remarks>
/// The destination changed in AC-543: this used to inject into whichever session was selected, cleaning the text
/// first for an SDK session and passing it raw to a TTY. Both of those went — there is one destination now and no
/// cleanup pass — so the cases asserting the old split were replaced rather than adapted.
/// </remarks>
public class OpenMicCoordinatorTests
{
    /// <summary>
    /// AC-543 criterion 20. This used to inject into whichever session happened to be selected — the reason it is
    /// asserted here rather than left to the assistant's own tests is that the destination is the whole change:
    /// an open microphone that still reached the selected session would put spoken asides into someone's prompt.
    /// </summary>
    [Fact]
    public async Task InjectUtteranceAsync_GoesToTheAssistant_NotTheSelectedSession()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out _, out _, assistant: assistant);

        await coordinator.InjectUtteranceAsync("what is the status of AC-223");

        await assistant.Received(1).SendAsync("what is the status of AC-223", Arg.Any<CancellationToken>());
    }

    /// <summary>Decision 10: what Whisper heard is what the assistant gets. No cleanup pass sits in between any more.</summary>
    [Fact]
    public async Task InjectUtteranceAsync_SendsTheRawTranscript_WithNoCleanupPass()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out _, out _, assistant: assistant);

        await coordinator.InjectUtteranceAsync("uh, pick up AC-222, no sorry, 223");

        // Verbatim, filler and self-correction included — reading through that is the model's job now.
        await assistant.Received(1).SendAsync("uh, pick up AC-222, no sorry, 223", Arg.Any<CancellationToken>());
    }

    /// <summary>A throat-clear the noise filter reduced to nothing must not become a turn the operator pays for.</summary>
    [Fact]
    public async Task InjectUtteranceAsync_AnEmptyUtterance_SendsNothing()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out _, out _, assistant: assistant);

        await coordinator.InjectUtteranceAsync("   ");

        await assistant.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
    }

    /// <summary>
    /// AC-627 criterion 3, and why this is not a one-line pause: an utterance the detector closed as the key went
    /// down is already inside the transcribe call and arrives afterwards. Pausing does not reach it.
    /// </summary>
    [Fact]
    public async Task AnUtteranceAlreadyOnItsWay_WhenAHoldTakesTheMicrophone_NeverReachesTheAssistant()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, assistant: assistant);
        await coordinator.StartAsync();
        coordinator.HandleSpeechStarted();

        using (coordinator.SuspendForHold())
        {
            // The transcript of what was said before the key went down, landing after it did.
            await coordinator.InjectUtteranceAsync("open the deployment notes");
        }

        await assistant.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
        listener.Received(1).Pause();
    }

    /// <summary>
    /// Always On is off, so a hold has nothing to take. Every hold asks anyway, so the answer belongs here — as a
    /// handle that does nothing coming and going.
    /// </summary>
    [Fact]
    public async Task AHoldWhileOpenMicIsOff_DoesNotTouchTheListenerAtAll()
    {
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });
        await coordinator.StartAsync();

        coordinator.SuspendForHold().Dispose();

        listener.DidNotReceiveWithAnyArgs().Pause();
        listener.DidNotReceiveWithAnyArgs().Resume();
    }

    /// <summary>Criterion 2: the hold ends and the microphone comes back on its own.</summary>
    [Fact]
    public async Task OnceTheHoldIsOver_TheMicrophoneComesBack()
    {
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        await coordinator.StartAsync();

        coordinator.SuspendForHold().Dispose();

        listener.Received(1).Pause();
        listener.Received(1).Resume();
    }

    /// <summary>And it is listening again for real — not merely un-paused with the drop still on.</summary>
    [Fact]
    public async Task AfterTheHold_TheNextUtteranceReachesTheAssistantAgain()
    {
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out _, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, assistant: assistant);
        await coordinator.StartAsync();
        coordinator.SuspendForHold().Dispose();

        await coordinator.InjectUtteranceAsync("what is the status of AC-627");

        await assistant.Received(1).SendAsync("what is the status of AC-627", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// AC-9 stays intact: a hold ending while read-aloud plays must not hand the microphone back, since the
    /// barge-in guard has its own reason to keep it paused.
    /// </summary>
    [Fact]
    public async Task AHoldEndingWhileReadAloudPlays_LeavesTheBargeInPauseAlone()
    {
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.SuspendForHold().Dispose();

        listener.DidNotReceive().Resume();
    }

    [Fact]
    public async Task StartAsync_OpenMicEnabled_PausesTheMicWhileReadAloudPlays()
    {
        var coordinator = _CreateCoordinator(out var listener, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });

        await coordinator.StartAsync();
        playbackQueue.PlaybackActiveChanged += Raise.Event<EventHandler<bool>>(playbackQueue, true);
        playbackQueue.PlaybackActiveChanged += Raise.Event<EventHandler<bool>>(playbackQueue, false);

        await listener.Received(1).StartAsync(Arg.Any<CancellationToken>());
        listener.Received(1).Pause();
        listener.Received(1).Resume();
    }

    [Fact]
    public async Task ReadAloudPlays_WithOpenMicOff_StillReportsButDoesNotPauseTheListener()
    {
        // The playback subscription is always on so the overlay's "speaking" pill shows for read-aloud even
        // without open-mic; but barge-in must not pause a microphone that is not listening.
        var coordinator = _CreateCoordinator(out var listener, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });
        await coordinator.StartAsync();

        playbackQueue.PlaybackActiveChanged += Raise.Event<EventHandler<bool>>(playbackQueue, true);

        listener.DidNotReceiveWithAnyArgs().Pause();
    }

    [Fact]
    public async Task StartAsync_OpenMicDisabled_NeverStartsTheListener()
    {
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });

        await coordinator.StartAsync();

        await listener.DidNotReceiveWithAnyArgs().StartAsync(default);
    }

    [Fact]
    public async Task ToggleOpenMic_StartsThenStopsTheListenerAtRuntime()
    {
        var coordinator = _CreateCoordinator(out var listener, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = false });
        await coordinator.StartAsync();

        await coordinator.ToggleOpenMicCommand.ExecuteAsync(null);
        Assert.True(coordinator.IsListening);

        await coordinator.ToggleOpenMicCommand.ExecuteAsync(null);
        Assert.False(coordinator.IsListening);

        await listener.Received(1).StartAsync(Arg.Any<CancellationToken>());
        await listener.Received(1).StopAsync();
    }

    [Fact]
    public async Task ToggleOpenMic_IsDisabledWhenVoiceIsOff()
    {
        var coordinator = _CreateCoordinator(out _, out _,
            new VoiceSettings { IsEnabled = false });
        await coordinator.StartAsync();

        Assert.False(coordinator.IsAvailable);
        Assert.False(coordinator.ToggleOpenMicCommand.CanExecute(null));
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
        var coordinator = _CreateCoordinator(out _, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, overlayCoordinator);
        await coordinator.StartAsync();

        Assert.Equal(VoiceOverlayState.Hidden, overlayCoordinator.Overlay.State);

        coordinator.HandleSpeechStarted();
        Assert.Equal(VoiceOverlayState.Listening, overlayCoordinator.Overlay.State);

        coordinator.HandleSpeechEnded();
        Assert.Equal(VoiceOverlayState.Transcribing, overlayCoordinator.Overlay.State);
    }

    /// <summary>The pill is released once the text lands, not when the speaking stopped — the cleanup pass runs in between.</summary>
    [Fact]
    public async Task OnceTheUtteranceIsInjected_ThePillGoesAway()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(out _, out _,
            overlay: overlayCoordinator);
        await coordinator.StartAsync();
        coordinator.HandleSpeechStarted();
        coordinator.HandleSpeechEnded();

        await coordinator.InjectUtteranceAsync("open the file");

        Assert.Equal(VoiceOverlayState.Hidden, overlayCoordinator.Overlay.State);
    }

    /// <summary>An utterance the assistant cannot take still ends. The alternative is a spinner over a sentence that is never coming.</summary>
    [Fact]
    public async Task WhenInjectingThrows_ThePillStillGoesAway()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var assistant = Substitute.For<IAssistantSessionHost>();
        var coordinator = _CreateCoordinator(out _, out _, overlay: overlayCoordinator, assistant: assistant);
        assistant.SendAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("the assistant is gone"));
        await coordinator.StartAsync();
        coordinator.HandleSpeechStarted();

        var act = () => coordinator.InjectUtteranceAsync("open the file");

        await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Equal(VoiceOverlayState.Hidden, overlayCoordinator.Overlay.State);
    }

    /// <summary>Read-aloud pauses the mic; the pill is how you see why it went quiet rather than wondering.</summary>
    [Fact]
    public async Task WhenReadAloudPlays_ThePillSaysSo()
    {
        var overlayCoordinator = new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter());
        var coordinator = _CreateCoordinator(out _, out _,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true }, overlayCoordinator);
        await coordinator.StartAsync();

        // Active but no audio yet = preparing (the local-LLM rewrite + text-to-sound synthesis).
        coordinator.HandlePlaybackActiveChanged(true);
        Assert.Equal(VoiceOverlayState.Preparing, overlayCoordinator.Overlay.State);

        // The first clip plays: now it is actually reading aloud.
        coordinator.HandleSpeakingStarted();
        Assert.Equal(VoiceOverlayState.Speaking, overlayCoordinator.Overlay.State);

        coordinator.HandlePlaybackActiveChanged(false);
        Assert.Equal(VoiceOverlayState.Hidden, overlayCoordinator.Overlay.State);
    }

    /// <summary>
    /// AC-9's microphone half: talking over read-aloud stops it. The hold half already worked and always has
    /// (<c>SessionPanelViewModel.BeginVoiceHold</c>) — a held key needs no threshold, because a room does not
    /// press one by accident. This is the half that has to guess, and it only guesses when asked.
    /// </summary>
    [Fact]
    public async Task TalkingOverReadAloud_StopsIt_WhenTheOperatorAskedForThat()
    {
        var coordinator = _CreateCoordinator(out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.4);
        coordinator.HandleSpeechStarted();

        playbackQueue.Received().StopAll();
    }

    [Fact]
    public async Task TheRoomIsNotTalking_SoAQuietMicrophoneLeavesReadAloudAlone()
    {
        var coordinator = _CreateCoordinator(out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.05);
        coordinator.HandleSpeechStarted();

        playbackQueue.DidNotReceive().StopAll();
    }

    /// <summary>Off by default: on speakers the microphone hears the read-aloud itself, and a threshold cannot tell that from you.</summary>
    [Fact]
    public async Task WithoutTheSetting_TalkingOverReadAloudDoesNothing()
    {
        var coordinator = _CreateCoordinator(out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true });
        await coordinator.StartAsync();
        coordinator.HandlePlaybackActiveChanged(true);

        coordinator.HandleAudioLevel(0.9);
        coordinator.HandleSpeechStarted();

        playbackQueue.DidNotReceive().StopAll();
    }

    /// <summary>Nothing is playing, so there is nothing to interrupt — talking is just dictation.</summary>
    [Fact]
    public async Task TalkingWhileNothingIsPlaying_StopsNothing()
    {
        var coordinator = _CreateCoordinator(out _, out var playbackQueue,
            new VoiceSettings { IsEnabled = true, OpenMicEnabled = true, StopReadAloudWhenSpeaking = true, StopReadAloudLevelThreshold = 0.15 });
        await coordinator.StartAsync();

        coordinator.HandleAudioLevel(0.9);
        coordinator.HandleSpeechStarted();

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

        await coordinator.StartAsync();

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
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

        Assert.Equal(0, listener.UtteranceSubscriberCount);
        Assert.False(coordinator.IsListening);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is InvalidOperationException);
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

        Assert.True(coordinator.IsAvailable);
        Assert.True(coordinator.ToggleOpenMicCommand.CanExecute(null));
    }

    /// <summary>
    /// AC-628, the coordinator's half: two enables landing together each opened a microphone. The settings load is
    /// held open rather than raced on timing, and the duplicate has to name where it came from.
    /// </summary>
    [Fact]
    public async Task EnableTwiceAtOnce_StartsTheListenerOnce_AndNamesWhereTheDuplicateCameFrom()
    {
        var settingsAreLoading = new TaskCompletionSource();
        var startIsHeldOpen = new TaskCompletionSource();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => _SettingsHeldUntil(settingsAreLoading));
        var listener = new FakeOpenMicListener { HoldStart = startIsHeldOpen };
        var logger = new CapturingLogger<OpenMicCoordinator>();
        var coordinator = _NewCoordinator(listener, voiceSettingsStore, logger);

        var first = coordinator.StartAsync();
        var second = coordinator.StartAsync();

        // Releasing the load lets the first through to the listener, where it is held — so the second asks while
        // the first has opened a microphone and not yet set IsListening.
        settingsAreLoading.SetResult();
        await _WaitUntilAsync(() => listener.StartCount >= 1);
        await _GiveASecondStartEveryChanceAsync(() => listener.StartCount >= 2);
        startIsHeldOpen.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, listener.StartCount);
        Assert.True(coordinator.IsListening);
        Assert.Contains(logger.Messages, message => message.Contains("already listening", StringComparison.OrdinalIgnoreCase)
            && message.Contains(nameof(OpenMicCoordinator.StartAsync), StringComparison.Ordinal));
    }

    private static async Task<VoiceSettings> _SettingsHeldUntil(TaskCompletionSource release)
    {
        await release.Task;
        return new VoiceSettings { IsEnabled = true, OpenMicEnabled = true };
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should become true within the poll window");
    }

    /// <summary>Waits out the moment a second start would land in, so the assertion that it did not is one the unfixed code fails.</summary>
    private static async Task _GiveASecondStartEveryChanceAsync(Func<bool> landed)
    {
        for (var i = 0; i < 20 && !landed(); i++)
        {
            await Task.Delay(10);
        }
    }

    private static OpenMicCoordinator _NewCoordinator(
        IOpenMicListener listener,
        IVoiceSettingsStore voiceSettingsStore,
        ILogger<OpenMicCoordinator> logger) =>
        new(listener,
            Substitute.For<IAssistantSessionHost>(),
            voiceSettingsStore,
            _AssistantOn(),
            Substitute.For<IVoicePlaybackQueue>(),
            new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            logger);

    /// <param name="overlay">Pass one to assert on the pill; omit it and the coordinator reports into a throwaway.</param>
    /// <param name="assistant">Where an utterance lands since AC-543 — the coordinator's only destination.</param>
    private static OpenMicCoordinator _CreateCoordinator(
        out IOpenMicListener listener,
        out IVoicePlaybackQueue playbackQueue,
        VoiceSettings? settings = null,
        VoiceOverlayCoordinator? overlay = null,
        IAssistantSessionHost? assistant = null)
    {
        listener = Substitute.For<IOpenMicListener>();
        playbackQueue = Substitute.For<IVoicePlaybackQueue>();
        assistant ??= Substitute.For<IAssistantSessionHost>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings ?? new VoiceSettings());
        return new OpenMicCoordinator(
            listener, assistant, voiceSettingsStore, _AssistantOn(), playbackQueue,
            overlay ?? new VoiceOverlayCoordinator(new VoiceOverlayViewModel(), new FakeVoiceOverlayPresenter()),
            NullLogger<OpenMicCoordinator>.Instance);
    }

    /// <summary>
    /// The assistant switched on. Open-mic is gated on both switches now, and every case here is about the
    /// microphone rather than about the feature flag — so the flag is held out of the way.
    /// </summary>
    private static IAssistantSettingsStore _AssistantOn()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AssistantSettings { IsEnabled = true });
        return store;
    }

}
