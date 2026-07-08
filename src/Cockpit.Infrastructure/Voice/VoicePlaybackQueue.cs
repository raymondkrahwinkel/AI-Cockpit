using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;

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

    private readonly Channel<(IReadOnlyList<string> Sentences, string VoiceId)> _channel =
        Channel.CreateUnbounded<(IReadOnlyList<string>, string)>();

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

        _channel.Writer.TryWrite((sentences, voiceId));
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
        await foreach (var (sentences, voiceId) in _channel.Reader.ReadAllAsync())
        {
            var cancellationToken = _playbackCancellation.Token;
            foreach (var sentence in sentences)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var audio = await _textToSpeech.SynthesizeAsync(sentence, voiceId, cancellationToken).ConfigureAwait(false);
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
        }
    }
}
