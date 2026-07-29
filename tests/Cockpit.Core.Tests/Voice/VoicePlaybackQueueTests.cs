using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
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

        queue.Enqueue(["First sentence.", "Second sentence."], speakerId: 1, language: "en");

        await _WaitUntilAsync(() => audioPlayback.CallCount >= 2);

        Assert.Equal(1, audioPlayback.MaxConcurrentCalls);
        Assert.Equal(new[] { "First sentence.", "Second sentence." }, textToSpeech.Calls.Select(call => call.Text));
        Assert.All(textToSpeech.Calls, call => Assert.True(call.SpeakerId == 1 && call.Language == "en"));
    }

    [Fact]
    public async Task Enqueue_EmptySentenceList_NeverCallsSynthesis()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue([], speakerId: 1, language: "en");
        await Task.Delay(30);

        Assert.Empty(textToSpeech.Calls);
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

        queue.Enqueue(["First sentence."], speakerId: 1, language: "en");
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        queue.StopAll();

        // Proves StopAll actually cancels the token passed into the in-flight PlayAsync call — not
        // just that draining the queue happens to leave CallCount looking right (that would pass even
        // if StopAll forgot to cancel anything, since the drain alone hides an un-cancelled hang).
        Assert.NotNull(capturedToken);
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

        queue.Enqueue(["First sentence."], speakerId: 1, language: "en");
        await playbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(["Never played."], speakerId: 1, language: "en");

        queue.StopAll();
        await Task.Delay(100);

        Assert.Equal(1, audioPlayback.CallCount);
        var call = Assert.Single(textToSpeech.Calls);
        Assert.Equal("First sentence.", call.Text);
    }

    [Fact]
    public async Task StopAll_ThenEnqueueAgain_PlaysNormally()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.StopAll();
        queue.Enqueue(["After a stop."], speakerId: 1, language: "en");

        await _WaitUntilAsync(() => audioPlayback.CallCount >= 1);

        var call = Assert.Single(textToSpeech.Calls);
        Assert.Equal("After a stop.", call.Text);
    }

    [Fact]
    public async Task Enqueue_SegmentsInDifferentLanguages_UsesOneSpeaker_AndInsertsNoSilence()
    {
        var textToSpeech = new FakeTextToSpeechService();
        var audioPlayback = new FakeAudioPlaybackService();
        var queue = new VoicePlaybackQueue(textToSpeech, audioPlayback, NullLogger<VoicePlaybackQueue>.Instance);

        queue.Enqueue(
        [
            new SpeechSegment(["Here is the answer."], "en"),
            new SpeechSegment(["Dit is het antwoord."], "nl"),
        ], speakerId: 2);

        await _WaitUntilAsync(() => textToSpeech.Calls.Count >= 2);

        // One multilingual voice: the same speaker synthesizes both segments, each in its own language.
        Assert.Equal(new[] { "en", "nl" }, textToSpeech.Calls.Select(call => call.Language));
        Assert.All(textToSpeech.Calls, call => Assert.Equal(2, call.SpeakerId));
        // No voice switch means no bridging silence — every played buffer is a spoken sentence.
        Assert.All(audioPlayback.PlayedBuffers, buffer => Assert.Contains(buffer, sample => sample != 0));
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

        queue.Enqueue(["A sentence."], speakerId: 1, language: "en");

        await _WaitUntilAsync(() =>
        {
            lock (states)
            {
                return states.Contains(false);
            }
        });
        lock (states)
        {
            Assert.Equal(new[] { true, false }, states);
        }
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
