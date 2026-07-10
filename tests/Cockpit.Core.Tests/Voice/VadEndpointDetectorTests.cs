using Cockpit.Core.Voice;
using FluentAssertions;

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
            detector.Observe(isSpeech: false, Frame).Should().Be(VadEndpointSignal.None);
        }

        detector.IsInSpeech.Should().BeFalse();
    }

    [Fact]
    public void Observe_ContiguousSpeechReachingMinimum_StartsExactlyOnce()
    {
        var detector = CreateDetector();

        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.SpeechStarted);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.IsInSpeech.Should().BeTrue();
    }

    [Fact]
    public void Observe_SpeechBlipShorterThanMinimumThenSilence_NeverStarts()
    {
        var detector = CreateDetector();

        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.Observe(isSpeech: false, Frame).Should().Be(VadEndpointSignal.None);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);

        detector.IsInSpeech.Should().BeFalse();
    }

    [Fact]
    public void Observe_ShortPauseWithinAnUtterance_DoesNotEndIt()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        // Silence shorter than the timeout (700ms < 800ms), then speech resumes.
        for (var i = 0; i < 7; i++)
        {
            detector.Observe(isSpeech: false, Frame).Should().Be(VadEndpointSignal.None);
        }

        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.IsInSpeech.Should().BeTrue();
    }

    [Fact]
    public void Observe_TrailingSilenceReachingTimeout_EndsTheUtterance()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        // 700ms of silence stays open; the 800ms observation closes it.
        for (var i = 0; i < 7; i++)
        {
            detector.Observe(isSpeech: false, Frame).Should().Be(VadEndpointSignal.None);
        }

        detector.Observe(isSpeech: false, Frame).Should().Be(VadEndpointSignal.SpeechEnded);
        detector.IsInSpeech.Should().BeFalse();
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

        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.SpeechStarted);
    }

    [Fact]
    public void Reset_DuringAnUtterance_ReturnsToWaitingForSpeech()
    {
        var detector = CreateDetector();
        _StartUtterance(detector);

        detector.Reset();

        detector.IsInSpeech.Should().BeFalse();
        // A fresh run of speech is needed again — the in-progress utterance was dropped.
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.None);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.SpeechStarted);
    }

    private static void _StartUtterance(VadEndpointDetector detector)
    {
        detector.Observe(isSpeech: true, Frame);
        detector.Observe(isSpeech: true, Frame).Should().Be(VadEndpointSignal.SpeechStarted);
    }
}
