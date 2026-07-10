using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="VoicePlaybackQueue"/>: sentences queued for read-aloud (#35) play back-to-back through
/// <see cref="FakeTextToSpeechService"/>/<see cref="FakeAudioPlaybackService"/>, never overlapping, and
/// <see cref="VoicePlaybackQueue.StopAll"/> interrupts whatever is currently playing and drops anything
/// still queued.
/// </summary>
public class VoicePlaybackQueueTests
{
    [Fact]
    public async Task Enqueue_TwoSentences_PlaysThemSequentially_NeverOverlapping()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService { OnPlay = _ => Task.Delay(30) };
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(["First sentence.", "Second sentence."], "en_US-lessac-medium");

        await _WaitUntilAsync(() => audioPlayback.CallCount >= 2);

        audioPlayback.MaxConcurrentCalls.Should().Be(1);
        textToSpeech.Calls.Select(call => call.Text).Should().Equal("First sentence.", "Second sentence.");
        textToSpeech.Calls.Should().OnlyContain(call => call.VoiceId == "en_US-lessac-medium");
    }

    [Fact]
    public async Task Enqueue_EmptySentenceList_NeverCallsSynthesis()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue([], "en_US-lessac-medium");
        await Task.Delay(30);

        textToSpeech.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StopAll_CancelsTheInFlightPlaybackToken()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var playbackStarted = new TaskCompletionSource();
        CancellationToken? capturedToken = null;
        var audioPlayback = new FakeAudioPlaybackService
        {
            OnPlay = cancellationToken =>
            {
                capturedToken = cancellationToken;
                playbackStarted.TrySetResult();
                return Task.Delay(Timeout.Infinite, cancellationToken);
            },
        };
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(["First sentence."], "en_US-lessac-medium");
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        queue.StopAll();

        // Proves StopAll actually cancels the token passed into the in-flight PlayAsync call — not
        // just that draining the queue happens to leave CallCount looking right (that would pass even
        // if StopAll forgot to cancel anything, since the drain alone hides an un-cancelled hang).
        capturedToken.Should().NotBeNull();
        await _WaitUntilAsync(() => capturedToken!.Value.IsCancellationRequested);
    }

    [Fact]
    public async Task StopAll_DropsAnythingStillQueued_BehindTheInFlightPlayback()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var playbackStarted = new TaskCompletionSource();
        var audioPlayback = new FakeAudioPlaybackService
        {
            OnPlay = async cancellationToken =>
            {
                playbackStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            },
        };
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(["First sentence."], "en_US-lessac-medium");
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(["Never played."], "en_US-lessac-medium");

        queue.StopAll();
        await Task.Delay(100);

        audioPlayback.CallCount.Should().Be(1);
        textToSpeech.Calls.Should().ContainSingle().Which.Text.Should().Be("First sentence.");
    }

    [Fact]
    public async Task StopAll_ThenEnqueueAgain_PlaysNormally()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.StopAll();
        queue.Enqueue(["After a stop."], "en_US-lessac-medium");

        await _WaitUntilAsync(() => audioPlayback.CallCount >= 1);

        textToSpeech.Calls.Should().ContainSingle().Which.Text.Should().Be("After a stop.");
    }

    [Fact]
    public async Task Enqueue_SegmentsWithDifferentVoices_PlaysEachVoiceAndInsertsSilenceBetween()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(
        [
            new SpeechSegment(["Here is the answer."], "en_US-lessac-medium"),
            new SpeechSegment(["Dit is het antwoord."], "nl_NL-ronnie-medium"),
        ]);

        await _WaitUntilAsync(() => textToSpeech.Calls.Count >= 2);

        textToSpeech.Calls.Select(call => call.VoiceId).Should().Equal("en_US-lessac-medium", "nl_NL-ronnie-medium");
        // The language switch is separated by an inserted all-zero (silent) buffer; the spoken sentences
        // are non-zero (FakeTextToSpeechService returns a small non-zero waveform).
        audioPlayback.PlayedBuffers.Should().Contain(buffer => buffer.Length > 0 && buffer.All(sample => sample == 0));
    }

    [Fact]
    public async Task Enqueue_SentencesInOneVoice_InsertsNoSilence()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(["First sentence.", "Second sentence."], "en_US-lessac-medium");

        await _WaitUntilAsync(() => audioPlayback.CallCount >= 2);

        // No language switch means no inserted silence — every played buffer is a spoken sentence.
        audioPlayback.PlayedBuffers.Should().OnlyContain(buffer => buffer.Any(sample => sample != 0));
    }

    [Fact]
    public async Task Enqueue_RaisesPlaybackActiveThenIdle_ForBargeIn()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);
        var states = new List<bool>();
        queue.PlaybackActiveChanged += (_, active) =>
        {
            lock (states)
            {
                states.Add(active);
            }
        };

        queue.Enqueue(["A sentence."], "en_US-lessac-medium");

        await _WaitUntilAsync(() =>
        {
            lock (states)
            {
                return states.Contains(false);
            }
        });
        lock (states)
        {
            states.Should().Equal(true, false);
        }
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
