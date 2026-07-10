using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// <see cref="IVoicePlaybackQueue"/>: a single background consumer synthesizes and plays queued
/// utterances one sentence at a time via <see cref="ITextToSpeechService"/> and
/// <see cref="IAudioPlaybackService"/>, so nothing ever overlaps. Registered as a singleton — one
/// shared queue for the whole (single-user) cockpit, so a push-to-talk hold on any session can
/// interrupt whichever session is currently talking (#35).
/// </summary>
// A classic constructor rather than the usual primary-constructor style (Code.md §12): the consumer
// loop must be started once, from real constructor logic, not just capture dependencies.
internal sealed class VoicePlaybackQueue : IVoicePlaybackQueue, ISingletonService
{
    private readonly ITextToSpeechService _textToSpeech;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly ILogger<VoicePlaybackQueue> _logger;

    // ~90ms of silence between two consecutive segments whose voice differs, so a Dutch/English switch
    // reads as a natural pause rather than the timbre jumping mid-breath.
    private static readonly TimeSpan InterLanguageGap = TimeSpan.FromMilliseconds(90);

    private readonly Channel<IReadOnlyList<SpeechSegment>> _channel =
        Channel.CreateUnbounded<IReadOnlyList<SpeechSegment>>();

    private CancellationTokenSource _playbackCancellation = new();
    private readonly Task _consumerTask;

    public VoicePlaybackQueue(ITextToSpeechService textToSpeech, IAudioPlaybackService audioPlayback, ILogger<VoicePlaybackQueue> logger)
    {
        _textToSpeech = textToSpeech;
        _audioPlayback = audioPlayback;
        _logger = logger;

        // Started once, for the lifetime of this singleton — no separate Start/Stop lifecycle needed
        // since the channel simply idles (ReadAllAsync awaits) until something is enqueued.
        _consumerTask = _ConsumeAsync();
    }

    public void Enqueue(IReadOnlyList<string> sentences, string voiceId)
    {
        if (sentences.Count == 0)
        {
            return;
        }

        Enqueue([new SpeechSegment(sentences, voiceId)]);
    }

    public void Enqueue(IReadOnlyList<SpeechSegment> segments)
    {
        var sentenceCount = segments.Sum(segment => segment.Sentences.Count);
        if (sentenceCount == 0)
        {
            return;
        }

        _logger.LogInformation("Read-aloud enqueued {Count} sentence(s) across {Segments} voice segment(s)", sentenceCount, segments.Count);
        _channel.Writer.TryWrite(segments);
    }

    public void StopAll()
    {
        // Swap in a fresh token so a later Enqueue plays normally again, then cancel + dispose the old
        // one — that cancels whatever SynthesizeAsync/PlayAsync call is currently in flight — and drain
        // anything still queued so it never starts.
        var previous = Interlocked.Exchange(ref _playbackCancellation, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
        while (_channel.Reader.TryRead(out _))
        {
        }
    }

    private async Task _ConsumeAsync()
    {
        await foreach (var segments in _channel.Reader.ReadAllAsync())
        {
            var cancellationToken = _playbackCancellation.Token;
            string? previousVoiceId = null;
            var lastSampleRate = 0;

            foreach (var segment in segments)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (previousVoiceId is not null && segment.VoiceId != previousVoiceId && lastSampleRate > 0)
                {
                    try
                    {
                        await _PlaySilenceAsync(lastSampleRate, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }

                foreach (var sentence in segment.Sentences)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        _logger.LogDebug("Read-aloud playing sentence: \"{Sentence}\"", sentence);
                        var audio = await _textToSpeech.SynthesizeAsync(sentence, segment.VoiceId, cancellationToken).ConfigureAwait(false);
                        lastSampleRate = audio.SampleRate;
                        var pcmBytes = PcmSampleConverter.ToInt16Bytes(audio.Samples);
                        await _audioPlayback.PlayAsync(pcmBytes, new AudioFormat(audio.SampleRate, Channels: 1), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "TTS playback failed for a queued sentence; skipping it.");
                    }
                }

                previousVoiceId = segment.VoiceId;
            }
        }
    }

    private Task _PlaySilenceAsync(int sampleRate, CancellationToken cancellationToken)
    {
        var silence = new byte[(int)(sampleRate * InterLanguageGap.TotalSeconds) * 2];
        return _audioPlayback.PlayAsync(silence, new AudioFormat(sampleRate, Channels: 1), cancellationToken);
    }
}
