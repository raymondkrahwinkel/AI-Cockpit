using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Voice;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The push-to-talk orchestration end to end, with fakes standing in for the microphone/VAD/STT so the tests
/// exercise the gating, chunking and wiring logic without any native runtime.
/// </summary>
public class VoicePushToTalkServiceTests
{
    // One analysis window = 300ms of 16 kHz mono s16 = 16000 * 0.3 * 2 bytes — same size the service windows
    // capture into internally. Each fake frame below is exactly one window, so the VAD is asked once per
    // frame and the return sequence drives the chunk boundaries (mirrors OpenMicListenerTests).
    private const int WindowBytes = 9600;

    [Fact]
    public async Task EndHoldAsync_LongHoldWithMidSilence_TranscribesMoreThanOnce_AndConcatenatesInRecordingOrder()
    {
        // Speech, a silence long enough to close the first chunk (3 windows >= the 800ms timeout), then more
        // speech that never gets released — closing the second chunk falls to EndHoldAsync's tail handling,
        // which is the 7th HasSpeechAsync call (6 windows during capture, then the tail gate).
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns(true, false, false, false, true, true, true);
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns("paragraph one", "paragraph two");
        var service = _CreateService(vad: vad, speechToText: speechToText, frames: _Windows(6));

        service.BeginHold();
        var result = await service.EndHoldAsync();

        Assert.Equal("paragraph one paragraph two", result);
        await speechToText.Received(2).TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EndHoldAsync_LaterChunkFinishesFirst_MergedTextStaysInRecordingOrder()
    {
        // Two chunks close entirely during the hold (each: speech, then 3 silent windows past the 800ms
        // timeout) — both TranscribeAsync calls are dispatched before EndHoldAsync ever runs. The second
        // chunk's worker reply arrives first; the merged text must still start with the first chunk's text.
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns(true, false, false, false, true, false, false, false);

        var firstChunk = new TaskCompletionSource<string>();
        var secondChunk = new TaskCompletionSource<string>();
        var dispatchCount = 0;
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++dispatchCount == 1 ? firstChunk.Task : secondChunk.Task);
        var service = _CreateService(vad: vad, speechToText: speechToText, frames: _Windows(8));

        service.BeginHold();
        var endHold = service.EndHoldAsync();

        secondChunk.SetResult("paragraph two");
        await Task.Delay(20);
        firstChunk.SetResult("paragraph one");
        var result = await endHold;

        Assert.Equal("paragraph one paragraph two", result);
    }

    [Fact]
    public async Task EndHoldAsync_NoSpeechDetected_ReturnsEmpty_AndNeverCallsSpeechToText()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(false);
        var speechToText = Substitute.For<ISpeechToTextService>();
        var service = _CreateService(vad: vad, speechToText: speechToText, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var result = await service.EndHoldAsync();

        Assert.Empty(result);
        await speechToText.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default);
    }

    [Fact]
    public async Task EndHoldAsync_SpeechDetected_ReturnsTheRawTranscript()
    {
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns("open the file");
        var service = _CreateService(speechToText: speechToText, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var result = await service.EndHoldAsync();

        Assert.Equal("open the file", result);
    }

    [Fact]
    public async Task EndHoldAsync_WhenTranscriptionFails_LogsError_AndRethrows()
    {
        // A failed first-use model download (Whisper/Silero are ~1.6 GB, fetched lazily) surfaces here as a
        // throw. Regression guard for the silent-failure bug: F9 looked like a dead hotkey because the fault
        // was caught in the view model and shown only as a status string, never logged.
        var boom = new InvalidOperationException("model download failed");
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromException<string>(boom));
        var logger = new CapturingLogger<VoicePushToTalkService>();
        var service = _CreateService(speechToText: speechToText, logger: logger, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var act = () => service.EndHoldAsync();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(act);
        Assert.Same(boom, thrown);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception == boom);
    }

    [Fact]
    public async Task AudioLevelSampled_FiresOncePerCapturedFrame_WhileHolding()
    {
        var service = _CreateService(frames: [[0, 0], [0xFF, 0x7F], [0, 0]]);
        var levels = new List<double>();
        service.AudioLevelSampled += (_, level) => levels.Add(level);

        service.BeginHold();
        await service.EndHoldAsync();

        Assert.Equal(3, System.Linq.Enumerable.Count(levels));
        Assert.All(levels, level => Assert.True(level >= 0 && level <= 1));
        Assert.True(levels[1] > levels[0]);
    }

    [Fact]
    public void BeginHold_CalledTwiceWithoutRelease_SecondCallReturnsFalse()
    {
        var service = _CreateService(frames: [[1, 0]]);

        Assert.True(service.BeginHold());
        Assert.False(service.BeginHold());
    }

    [Fact]
    public async Task EndHoldAsync_WithoutBeginHold_Throws()
    {
        var service = _CreateService();

        var act = () => service.EndHoldAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(act);
    }

    [Fact]
    public async Task BeginHold_AfterAPriorHoldEnded_SucceedsAgain()
    {
        var service = _CreateService(frames: [[1, 0]]);
        service.BeginHold();
        await service.EndHoldAsync();

        Assert.True(service.BeginHold());
    }

    private static VoicePushToTalkService _CreateService(
        IVoiceActivityDetector? vad = null,
        ISpeechToTextService? speechToText = null,
        ILogger<VoicePushToTalkService>? logger = null,
        params byte[][] frames)
    {
        vad ??= _AlwaysDetectsSpeech();
        speechToText ??= Substitute.For<ISpeechToTextService>();

        return new VoicePushToTalkService(
            new FakeAudioCaptureService(frames),
            vad,
            speechToText,
            logger ?? NullLogger<VoicePushToTalkService>.Instance);
    }

    private static IVoiceActivityDetector _AlwaysDetectsSpeech()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(true);
        return vad;
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
}
