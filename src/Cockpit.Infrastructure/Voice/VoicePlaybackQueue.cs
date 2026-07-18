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

    /// <summary>One queued read-aloud batch: language-tagged segments plus the single speaker (sid) that voices them all.</summary>
    private sealed record QueuedUtterance(IReadOnlyList<SpeechSegment> Segments, int SpeakerId);

    private readonly Channel<QueuedUtterance> _channel = Channel.CreateUnbounded<QueuedUtterance>();

    private CancellationTokenSource _playbackCancellation = new();
    private readonly Task _consumerTask;

    // Flipped by both the consumer loop and NotifyPreparing/StopAll (UI thread), so all three fields below are
    // guarded by _activeGate.
    private readonly object _activeGate = new();
    private bool _isPlaybackActive;

    // Whether SpeakingStarted has fired for the current active window — reset when it goes idle, so the next
    // read-aloud announces its preparing→speaking boundary again.
    private bool _speakingAnnounced;

    // Bumped by StopAll so a caller preparing a batch can tell a barge-in cancelled it mid-rewrite (see Generation).
    private int _generation;

    public event EventHandler<bool>? PlaybackActiveChanged;

    public event EventHandler? SpeakingStarted;

    public int Generation
    {
        get
        {
            lock (_activeGate)
            {
                return _generation;
            }
        }
    }

    public VoicePlaybackQueue(ITextToSpeechService textToSpeech, IAudioPlaybackService audioPlayback, ILogger<VoicePlaybackQueue> logger)
    {
        _textToSpeech = textToSpeech;
        _audioPlayback = audioPlayback;
        _logger = logger;

        // Started once, for the lifetime of this singleton — no separate Start/Stop lifecycle needed
        // since the channel simply idles (ReadAllAsync awaits) until something is enqueued.
        _consumerTask = _ConsumeAsync();
    }

    public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language)
    {
        if (sentences.Count == 0)
        {
            return;
        }

        Enqueue([new SpeechSegment(sentences, language)], speakerId);
    }

    public void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId)
    {
        var sentenceCount = segments.Sum(segment => segment.Sentences.Count);
        if (sentenceCount == 0)
        {
            return;
        }

        _logger.LogInformation("Read-aloud enqueued {Count} sentence(s) across {Segments} language segment(s)", sentenceCount, segments.Count);
        _channel.Writer.TryWrite(new QueuedUtterance(segments, speakerId));
    }

    public void NotifyPreparing() => _SetPlaybackActive(true);

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

        // Bump the generation so a batch still preparing (awaiting the local-LLM rewrite) drops itself instead of
        // enqueuing over this interrupt once it comes back.
        lock (_activeGate)
        {
            _generation++;
        }

        // Clear a "preparing" pill that never reached playback (barge-in during the local-LLM/synth gap).
        _SetPlaybackActive(false);
    }

    private async Task _ConsumeAsync()
    {
        await foreach (var utterance in _channel.Reader.ReadAllAsync())
        {
            _SetPlaybackActive(true);
            await _PlayUtteranceAsync(utterance, _playbackCancellation.Token).ConfigureAwait(false);

            // Only go idle once nothing is waiting behind this batch, so back-to-back read-aloud turns do
            // not flap the barge-in guard off and on between them.
            if (_channel.Reader.Count == 0)
            {
                _SetPlaybackActive(false);
            }
        }
    }

    private async Task _PlayUtteranceAsync(QueuedUtterance utterance, CancellationToken cancellationToken)
    {
        // One sentence plays at a time, but the next is synthesized while the current one plays: sherpa-onnx
        // synthesis is a CPU-bound call, and doing it strictly between plays left an audible gap (the synth time)
        // between sentences, which read as unnatural. Prefetching one ahead overlaps synth with playback so the
        // sentences run together.
        var items = utterance.Segments
            .SelectMany(segment => segment.Sentences.Select(sentence => (Text: sentence, segment.Language)))
            .ToList();
        if (items.Count == 0)
        {
            return;
        }

        var pending = _TrySynthesizeAsync(items[0].Text, utterance.SpeakerId, items[0].Language, cancellationToken);
        for (var i = 0; i < items.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var audio = await pending.ConfigureAwait(false);

            // Kick off the next synthesis before playing the current clip, so the two overlap and no silence opens up.
            pending = i + 1 < items.Count
                ? _TrySynthesizeAsync(items[i + 1].Text, utterance.SpeakerId, items[i + 1].Language, cancellationToken)
                : Task.FromResult<TtsAudio?>(null);

            if (audio is null)
            {
                continue;
            }

            // The first real clip of this window is the preparing→speaking boundary: until now it was synthesizing
            // in silence, and the overlay should switch from "preparing" to "reading aloud" exactly here. The
            // check-and-set is atomic under the gate because a UI-thread StopAll can reset the flag concurrently.
            bool announce;
            lock (_activeGate)
            {
                announce = !_speakingAnnounced;
                _speakingAnnounced = true;
            }

            if (announce)
            {
                SpeakingStarted?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                var pcmBytes = PcmSampleConverter.ToInt16Bytes(audio.Samples);
                await _audioPlayback.PlayAsync(pcmBytes, new AudioFormat(audio.SampleRate, Channels: 1), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Synthesizes one sentence, swallowing a failure to null so a single bad sentence neither kills the utterance nor faults the prefetch task waiting behind it.</summary>
    private async Task<TtsAudio?> _TrySynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Read-aloud synthesizing sentence: \"{Sentence}\"", text);
            return await _textToSpeech.SynthesizeAsync(text, speakerId, language, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS synthesis failed for a queued sentence; skipping it.");
            return null;
        }
    }

    private void _SetPlaybackActive(bool active)
    {
        // NotifyPreparing (UI thread) and the consumer loop both flip this, so the transition is guarded — the
        // event is raised outside the lock so a handler can never deadlock against it.
        lock (_activeGate)
        {
            if (_isPlaybackActive == active)
            {
                return;
            }

            _isPlaybackActive = active;

            // Going idle ends the window: the next read-aloud starts preparing again before it speaks. Reset under
            // the same gate that guards the announce check-and-set above.
            if (!active)
            {
                _speakingAnnounced = false;
            }
        }

        PlaybackActiveChanged?.Invoke(this, active);
    }
}
