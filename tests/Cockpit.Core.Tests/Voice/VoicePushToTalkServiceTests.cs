using Cockpit.Core.Abstractions.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The push-to-talk orchestration end to end, with fakes standing in for the microphone/VAD/STT/
/// cleanup so the tests exercise the gating and wiring logic without any native runtime: silence never
/// reaches STT, cleanup is only applied when asked for, and the hold guard is enforced.
/// </summary>
public class VoicePushToTalkServiceTests
{
    [Fact]
    public async Task EndHoldAsync_NoSpeechDetected_ReturnsEmpty_AndNeverCallsSpeechToText()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(false);
        var speechToText = Substitute.For<ISpeechToTextService>();
        var service = _CreateService(vad: vad, speechToText: speechToText, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var result = await service.EndHoldAsync(applyCleanup: false);

        result.Should().BeEmpty();
        await speechToText.DidNotReceiveWithAnyArgs().TranscribeAsync(default!, default);
    }

    [Fact]
    public async Task EndHoldAsync_SpeechDetected_ReturnsRawTranscript_WhenCleanupNotApplied()
    {
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns("open the file");
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        var service = _CreateService(speechToText: speechToText, cleanup: cleanup, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var result = await service.EndHoldAsync(applyCleanup: false);

        result.Should().Be("open the file");
        await cleanup.DidNotReceiveWithAnyArgs().CleanupAsync(default!, default);
    }

    [Fact]
    public async Task EndHoldAsync_SpeechDetected_RunsCleanup_WhenApplyCleanupIsTrue()
    {
        var speechToText = Substitute.For<ISpeechToTextService>();
        speechToText.TranscribeAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns("open the file");
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.CleanupAsync("open the file", Arg.Any<CancellationToken>()).Returns("Open the file.");
        var service = _CreateService(speechToText: speechToText, cleanup: cleanup, frames: [[1, 0, 2, 0]]);

        service.BeginHold();
        var result = await service.EndHoldAsync(applyCleanup: true);

        result.Should().Be("Open the file.");
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
        var act = () => service.EndHoldAsync(applyCleanup: false);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);
        logger.Entries.Should().Contain(entry => entry.Level == LogLevel.Error && entry.Exception == boom);
    }

    [Fact]
    public async Task AudioLevelSampled_FiresOncePerCapturedFrame_WhileHolding()
    {
        var service = _CreateService(frames: [[0, 0], [0xFF, 0x7F], [0, 0]]);
        var levels = new List<double>();
        service.AudioLevelSampled += (_, level) => levels.Add(level);

        service.BeginHold();
        await service.EndHoldAsync(applyCleanup: false);

        levels.Should().HaveCount(3);
        levels.Should().OnlyContain(level => level >= 0 && level <= 1);
        levels[1].Should().BeGreaterThan(levels[0]);
    }

    [Fact]
    public void BeginHold_CalledTwiceWithoutRelease_SecondCallReturnsFalse()
    {
        var service = _CreateService(frames: [[1, 0]]);

        service.BeginHold().Should().BeTrue();
        service.BeginHold().Should().BeFalse();
    }

    [Fact]
    public async Task EndHoldAsync_WithoutBeginHold_Throws()
    {
        var service = _CreateService();

        var act = () => service.EndHoldAsync(applyCleanup: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task BeginHold_AfterAPriorHoldEnded_SucceedsAgain()
    {
        var service = _CreateService(frames: [[1, 0]]);
        service.BeginHold();
        await service.EndHoldAsync(applyCleanup: false);

        service.BeginHold().Should().BeTrue();
    }

    private static VoicePushToTalkService _CreateService(
        IVoiceActivityDetector? vad = null,
        ISpeechToTextService? speechToText = null,
        ITranscriptCleanupService? cleanup = null,
        ILogger<VoicePushToTalkService>? logger = null,
        params byte[][] frames)
    {
        vad ??= _AlwaysDetectsSpeech();
        speechToText ??= Substitute.For<ISpeechToTextService>();
        cleanup ??= Substitute.For<ITranscriptCleanupService>();

        return new VoicePushToTalkService(
            new FakeAudioCaptureService(frames),
            vad,
            speechToText,
            cleanup,
            logger ?? NullLogger<VoicePushToTalkService>.Instance);
    }

    private static IVoiceActivityDetector _AlwaysDetectsSpeech()
    {
        var vad = Substitute.For<IVoiceActivityDetector>();
        vad.HasSpeechAsync(Arg.Any<float[]>(), Arg.Any<CancellationToken>()).Returns(true);
        return vad;
    }

}
