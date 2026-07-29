using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="VadEndpointDetector"/>: the pure endpointing logic for open-mic dictation — an utterance
/// starts once enough contiguous speech accumulates and ends once the trailing silence reaches the
/// timeout, with lone noise blips never opening one.
/// </summary>
public class VadEndpointDetectorTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SilenceTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan MinSpeechToStart = TimeSpan.FromMilliseconds(200);

    private static VadEndpointDetector CreateDetector() => new(SilenceTimeout, MinSpeechToStart);

    [Fact]
    public void Observe_SilenceOnly_NeverStartsAnUtterance()
    {
        var detector = CreateDetector();

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: false, Frame));
        }

        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void Observe_ContiguousSpeechReachingMinimum_StartsExactlyOnce()
    {
        var detector = CreateDetector();

        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.Equal(VadEndpointSignal.SpeechStarted, detector.Observe(isSpeech: true, Frame));
        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.True(detector.IsInSpeech);
    }

    [Fact]
    public void Observe_SpeechBlipShorterThanMinimumThenSilence_NeverStarts()
    {
        var detector = CreateDetector();

        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: false, Frame));
        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));

        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void Observe_ShortPauseWithinAnUtterance_DoesNotEndIt()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        // Silence shorter than the timeout (700ms < 800ms), then speech resumes.
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: false, Frame));
        }

        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.True(detector.IsInSpeech);
    }

    [Fact]
    public void Observe_TrailingSilenceReachingTimeout_EndsTheUtterance()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        // 700ms of silence stays open; the 800ms observation closes it.
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: false, Frame));
        }

        Assert.Equal(VadEndpointSignal.SpeechEnded, detector.Observe(isSpeech: false, Frame));
        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void Observe_SecondUtteranceAfterTheFirstEnds_StartsAgain()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);
        for (var i = 0; i < 8; i++)
        {
            detector.Observe(isSpeech: false, Frame);
        }

        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.Equal(VadEndpointSignal.SpeechStarted, detector.Observe(isSpeech: true, Frame));
    }

    [Fact]
    public void Reset_DuringAnUtterance_ReturnsToWaitingForSpeech()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        detector.Reset();

        Assert.False(detector.IsInSpeech);
        // A fresh run of speech is needed again — the in-progress utterance was dropped.
        Assert.Equal(VadEndpointSignal.None, detector.Observe(isSpeech: true, Frame));
        Assert.Equal(VadEndpointSignal.SpeechStarted, detector.Observe(isSpeech: true, Frame));
    }

    private static void _StartUtterance(VadEndpointDetector detector)
    {
        detector.Observe(isSpeech: true, Frame);
        Assert.Equal(VadEndpointSignal.SpeechStarted, detector.Observe(isSpeech: true, Frame));
    }
}
