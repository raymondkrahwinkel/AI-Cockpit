using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Audio;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

internal sealed class VoicePlaybackQueue : IVoicePlaybackQueue, ISingletonService
{
    private sealed class QueuedBatch(int total, DateTimeOffset enqueuedAt)
    {
        private int _completed;
        private int _played;
        private int _aborted;

        public int Total { get; } = total;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();

        public (bool IsComplete, int Played, bool Aborted) Complete(bool played, bool aborted)
        {
            if (played)
            {
                Interlocked.Increment(ref _played);
            }
            if (aborted)
            {
                Interlocked.Exchange(ref _aborted, 1);
            }

            var completed = Interlocked.Increment(ref _completed);
            return (completed == Total, Volatile.Read(ref _played), Volatile.Read(ref _aborted) != 0);
        }
    }

    private sealed record QueuedSentence(QueuedBatch Batch, string Text, string Language, int SpeakerId, VoicePlaybackSource Source, int Generation);
    private sealed record PreparedSentence(QueuedSentence Sentence, TtsAudio? Audio);

    private const int PreparedSentenceCapacity = 3;
    private readonly ITextToSpeechService _textToSpeech;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly ILogger<VoicePlaybackQueue> _logger;
    private readonly IAudioAvailability? _audioAvailability;
    private readonly Channel<QueuedSentence> _sentences = Channel.CreateUnbounded<QueuedSentence>();
    private readonly Channel<PreparedSentence> _prepared = Channel.CreateBounded<PreparedSentence>(new BoundedChannelOptions(PreparedSentenceCapacity)
    {
        SingleReader = true,
        SingleWriter = true,
    });
    private readonly object _activeGate = new();
    private readonly Task _preparationTask;
    private readonly Task _consumerTask;
    private CancellationTokenSource _playbackCancellation = new();
    private int _queuedSentenceCount;
    private long _lastPlaybackEnded;
    private bool _isPlaybackActive;
    private bool _speakingAnnounced;
    private int _generation;
    private VoicePlaybackSource _activeSource;

    public event EventHandler<bool>? PlaybackActiveChanged;
    public event EventHandler? SpeakingStarted;
    public event EventHandler? PlaybackStopped;

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

    public VoicePlaybackSource ActiveSource
    {
        get
        {
            lock (_activeGate)
            {
                return _activeSource;
            }
        }
    }

    public VoicePlaybackQueue(
        ITextToSpeechService textToSpeech,
        IAudioPlaybackService audioPlayback,
        ILogger<VoicePlaybackQueue> logger,
        IAudioAvailability? audioAvailability = null)
    {
        _textToSpeech = textToSpeech;
        _audioPlayback = audioPlayback;
        _logger = logger;
        _audioAvailability = audioAvailability;
        _preparationTask = _PrepareAsync();
        _consumerTask = _ConsumeAsync();
    }

    public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        if (sentences.Count > 0)
        {
            Enqueue([new SpeechSegment(sentences, language)], speakerId, source);
        }
    }

    public void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId, VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        if (_audioAvailability?.IsAvailable == false)
        {
            return;
        }

        var sentenceCount = segments.Sum(segment => segment.Sentences.Count);
        if (sentenceCount == 0)
        {
            return;
        }

        _logger.LogInformation("Read-aloud enqueued {Count} sentence(s) across {Segments} language segment(s); {QueueDepth} sentence(s) already queued.",
            sentenceCount, segments.Count, _sentences.Reader.Count);
        var batch = new QueuedBatch(sentenceCount, DateTimeOffset.UtcNow);
        var generation = Generation;
        foreach (var segment in segments)
        {
            foreach (var sentence in segment.Sentences)
            {
                Interlocked.Increment(ref _queuedSentenceCount);
                _sentences.Writer.TryWrite(new QueuedSentence(batch, sentence, segment.Language, speakerId, source, generation));
            }
        }
    }

    public void NotifyPreparing(VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        if (_audioAvailability?.IsAvailable != false)
        {
            _SetPlaybackActive(true, source);
        }
    }

    public void StopAll()
    {
        lock (_activeGate)
        {
            _generation++;
        }

        var previous = Interlocked.Exchange(ref _playbackCancellation, new CancellationTokenSource());
        previous.Cancel();
        previous.Dispose();
        while (_sentences.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queuedSentenceCount);
        }
        while (_prepared.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queuedSentenceCount);
        }
        Interlocked.Exchange(ref _lastPlaybackEnded, 0);
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
        _SetPlaybackActive(false);
    }

    private async Task _PrepareAsync()
    {
        await foreach (var sentence in _sentences.Reader.ReadAllAsync())
        {
            var cancellationToken = _playbackCancellation.Token;
            if (sentence.Generation != Generation || cancellationToken.IsCancellationRequested)
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            var audio = await _TrySynthesizeAsync(sentence.Text, sentence.SpeakerId, sentence.Language, cancellationToken)
                .ConfigureAwait(false);
            if (sentence.Generation != Generation || cancellationToken.IsCancellationRequested)
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            try
            {
                await _prepared.Writer.WriteAsync(new PreparedSentence(sentence, audio), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _Complete(sentence, played: false, aborted: true);
            }
        }
    }

    private async Task _ConsumeAsync()
    {
        await foreach (var prepared in _prepared.Reader.ReadAllAsync())
        {
            var sentence = prepared.Sentence;
            if (sentence.Generation != Generation)
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            _SetPlaybackActive(true, sentence.Source);
            if (prepared.Audio is null)
            {
                _Complete(sentence, played: false, aborted: false);
                continue;
            }

            var previousEnd = Interlocked.Read(ref _lastPlaybackEnded);
            if (previousEnd == 0)
            {
                _logger.LogInformation("Read-aloud first sound after {FirstSoundMs} ms.",
                    (DateTimeOffset.UtcNow - sentence.Batch.EnqueuedAt).TotalMilliseconds);
            }
            else
            {
                _logger.LogInformation("Read-aloud gap since the prior sentence was {GapMs} ms.",
                    Stopwatch.GetElapsedTime(previousEnd).TotalMilliseconds);
            }

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
                var pcmBytes = PcmSampleConverter.ToInt16Bytes(prepared.Audio.Samples);
                await _audioPlayback.PlayAsync(pcmBytes, new AudioFormat(prepared.Audio.SampleRate, Channels: 1), _playbackCancellation.Token)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _lastPlaybackEnded, Stopwatch.GetTimestamp());
                _Complete(sentence, played: true, aborted: false);
            }
            catch (OperationCanceledException)
            {
                _Complete(sentence, played: false, aborted: true);
            }
        }
    }

    private void _Complete(QueuedSentence sentence, bool played, bool aborted)
    {
        var completed = sentence.Batch.Complete(played, aborted);
        Interlocked.Decrement(ref _queuedSentenceCount);
        if (completed.IsComplete)
        {
            _logger.LogInformation("Read-aloud played {SentencesPlayed}/{TotalSentences} sentence(s) in {ElapsedMs} ms{Aborted}.",
                completed.Played, sentence.Batch.Total, sentence.Batch.Stopwatch.ElapsedMilliseconds, completed.Aborted ? " (aborted)" : string.Empty);
        }

        if (Volatile.Read(ref _queuedSentenceCount) == 0)
        {
            _SetPlaybackActive(false);
        }
    }

    private async Task<TtsAudio?> _TrySynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Read-aloud synthesizing a sentence of {Length} chars.", text.Length);
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

    private void _SetPlaybackActive(bool active, VoicePlaybackSource source = VoicePlaybackSource.Session)
    {
        lock (_activeGate)
        {
            if (_isPlaybackActive == active)
            {
                return;
            }

            _isPlaybackActive = active;
            if (active)
            {
                _activeSource = source;
            }
            else
            {
                _speakingAnnounced = false;
            }
        }

        PlaybackActiveChanged?.Invoke(this, active);
    }
}
