using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
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

        transcripts.Should().ContainSingle().Which.Should().Be("open the file");
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

        condition().Should().BeTrue("the condition should become true within the poll window");
    }
}
