using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="OpenMicListener"/> orchestration with fakes for the mic/VAD/STT: a speech-then-silence run
/// through the analysis windows produces exactly one transcribed utterance, and a paused listener drops
/// its audio so read-aloud never gets transcribed back (barge-in).
/// </summary>
public class OpenMicListenerTests
{
    // One analysis window = 300ms of 16 kHz mono s16 = 16000 * 0.3 * 2 bytes. Each fake frame is exactly
    // one window, so the VAD is asked once per frame and the return sequence drives the endpointing.
    private const int WindowBytes = 9600;

    [Fact]
    public async Task Listen_SpeechThenSilenceReachingTimeout_RaisesOneTranscribedUtterance()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns(false, true, true, false, false, false);
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns("open the file");
        var listener = _CreateListener(vad, speechToText, _Windows(6));
        var transcripts = new List<string>();
        listener.UtteranceTranscribed += (_, text) => transcripts.Add(text);

        await listener.StartAsync();
        await _WaitUntilAsync(() => transcripts.Count >= 1);
        await listener.StopAsync();

        var transcript = Assert.Single(transcripts);
        Assert.Equal("open the file", transcript);
    }

    [Fact]
    public async Task Listen_WhilePaused_DropsAudioAndNeverTranscribes()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(true);
        var speechToText = Substitute.For<ISpeechToTextService>();
        var listener = _CreateListener(vad, speechToText, _Windows(6));

        listener.Pause();
        await listener.StartAsync();
        await Task.Delay(100);
        await listener.StopAsync();

        await speechToText.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default);
    }

    /// <summary>
    /// AC-707: transcribing the first utterance used to be awaited inside the capture loop, so the mic stopped
    /// being read for as long as Whisper took — a second utterance spoken during that window was lost. Holding
    /// the first clip open here proves the second utterance is still detected and transcribed while the first
    /// is still in flight, and that the two transcriptions never overlap (the gate the worker already has).
    /// </summary>
    [Fact]
    public async Task Listen_SecondUtteranceArrivesWhileFirstStillTranscribing_BothAreTranscribedWithoutOverlap()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns(true, false, false, false, true, false, false, false);
        var speechToText = new GatedSpeechToTextService();
        var firstClipGate = new TaskCompletionSource();
        var firstClipStarted = new TaskCompletionSource();
        speechToText.OnTranscribe = async (index, _) =>
        {
            if (index != 0)
            {
                return "second";
            }

            firstClipStarted.TrySetResult();
            await firstClipGate.Task;
            return "first";
        };
        var listener = _CreateListener(vad, speechToText, _Windows(8));
        var speechStartedCount = 0;
        listener.SpeechStarted += (_, _) => Interlocked.Increment(ref speechStartedCount);
        var transcripts = new List<string>();
        listener.UtteranceTranscribed += (_, text) => transcripts.Add(text);

        await listener.StartAsync();
        await firstClipStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The capture loop reached the second utterance and asked the (still gated) worker to transcribe it too —
        // proof the mic kept being read instead of stalling behind the first clip's still-pending transcription.
        await _WaitUntilAsync(() => speechStartedCount == 2);

        firstClipGate.SetResult();
        await _WaitUntilAsync(() => transcripts.Count == 2);
        await listener.StopAsync();

        Assert.Equal(["first", "second"], transcripts);
        Assert.Equal(1, speechToText.MaxConcurrentCalls);
    }

    /// <summary>
    /// AC-628: starts landing together each opened a microphone. The settings load is held open rather than raced
    /// on timing, so the second call is provably inside the window the guard used to leave open.
    /// </summary>
    [Fact]
    public async Task StartAsync_TwiceAtOnce_OpensOneCaptureDeviceAndSaysTheSecondStartDidNothing()
    {
        var settingsAreLoading = new TaskCompletionSource();
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => _SettingsHeldUntil(settingsAreLoading));
        var capture = new FakeAudioCaptureService(_Windows(2));
        var logger = new CapturingLogger<OpenMicListener>();
        var listener = new OpenMicListener(
            capture, Substitute.For<IVoiceActivityDetector>(), Substitute.For<ISpeechToTextService>(), settingsStore, logger);

        var first = listener.StartAsync();
        var second = listener.StartAsync();
        settingsAreLoading.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, capture.CaptureCount);
        Assert.Contains(logger.Messages, message => message.Contains("already listening", StringComparison.OrdinalIgnoreCase));
        await listener.StopAsync();
    }

    /// <summary>The other half of AC-628: a stop has to close what is running, so "Always on" off leaves no open microphone behind.</summary>
    [Fact]
    public async Task StopAsync_ClosesTheCapture_SoNoMicrophoneIsLeftOpen()
    {
        var capture = new FakeAudioCaptureService(_Windows(2));
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());
        var listener = new OpenMicListener(
            capture, Substitute.For<IVoiceActivityDetector>(), Substitute.For<ISpeechToTextService>(), settingsStore,
            NullLogger<OpenMicListener>.Instance);

        await listener.StartAsync();
        await _WaitUntilAsync(() => capture.ActiveCaptures == 1);
        await listener.StopAsync();

        Assert.Equal(0, capture.ActiveCaptures);
    }

    private static async Task<VoiceSettings> _SettingsHeldUntil(TaskCompletionSource release)
    {
        await release.Task;
        return new VoiceSettings();
    }

    private static OpenMicListener _CreateListener(IVoiceActivityDetector vad, ISpeechToTextService speechToText, byte[][] frames)
    {
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings());
        return new OpenMicListener(
            new FakeAudioCaptureService(frames),
            vad,
            speechToText,
            settingsStore,
            NullLogger<OpenMicListener>.Instance);
    }

    private static byte[][] _Windows(int count)
    {
        var frames = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            frames[i] = new byte[WindowBytes];
        }

        return frames;
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should become true within the poll window");
    }
}
