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

    private sealed record QueuedSentence(QueuedBatch Batch, string Text, string Language, int SpeakerId, VoicePlaybackSource Source, string? OwnerId, int Generation, CancellationToken CancellationToken);
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
    private bool _audioPending;
    private int _generation;
    private VoicePlaybackSource _activeSource;
    private string? _activeOwnerId;
    private readonly HashSet<(VoicePlaybackSource Source, string? OwnerId)> _openStreams = [];

    public event EventHandler<bool>? PlaybackActiveChanged;
    public event EventHandler? SpeakingStarted;
    public event EventHandler<bool>? AudioPendingChanged;
    public event EventHandler<VoicePlaybackStoppedEventArgs>? PlaybackStopped;

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

    public string? ActiveOwnerId
    {
        get { lock (_activeGate) { return _activeOwnerId; } }
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

    public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, VoicePlaybackSource source = VoicePlaybackSource.Session, string? ownerId = null)
    {
        if (sentences.Count > 0)
        {
            Enqueue([new SpeechSegment(sentences, language)], speakerId, source, ownerId);
        }
    }

    public void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId, VoicePlaybackSource source = VoicePlaybackSource.Session, string? ownerId = null)
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
        var generation = _CaptureGeneration();
        Interlocked.Add(ref _queuedSentenceCount, sentenceCount);
        _SetAudioPending(true);
        foreach (var segment in segments)
        {
            foreach (var sentence in segment.Sentences)
            {
                _sentences.Writer.TryWrite(new QueuedSentence(batch, sentence, segment.Language, speakerId, source, ownerId, generation.Generation, generation.Token));
            }
        }
    }

    public void NotifyPreparing(VoicePlaybackSource source = VoicePlaybackSource.Session, string? ownerId = null)
    {
        if (_audioAvailability?.IsAvailable != false)
        {
            lock (_activeGate)
            {
                _openStreams.Add((source, ownerId));
            }
            _SetPlaybackActive(true, source, ownerId);
        }
    }

    public void CompleteStream(VoicePlaybackSource source, string? ownerId)
    {
        lock (_activeGate)
        {
            _openStreams.Remove((source, ownerId));
        }
        _SetInactiveIfDrained();
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_audioAvailability?.IsAvailable != false)
        {
            await _textToSpeech.WarmUpAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void StopAll()
    {
        VoicePlaybackStoppedEventArgs stopped;
        CancellationTokenSource previous;
        lock (_activeGate)
        {
            _generation++;
            previous = _playbackCancellation;
            _playbackCancellation = new CancellationTokenSource();
            stopped = new VoicePlaybackStoppedEventArgs(_activeSource, _activeOwnerId);
            _openStreams.Clear();
        }
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
        PlaybackStopped?.Invoke(this, stopped);
        _SetAudioPending(false);
        _SetPlaybackActive(false);
    }

    private async Task _PrepareAsync()
    {
        await foreach (var sentence in _sentences.Reader.ReadAllAsync())
        {
            if (!_IsCurrent(sentence))
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            var audio = await _TrySynthesizeAsync(sentence.Text, sentence.SpeakerId, sentence.Language, sentence.CancellationToken)
                .ConfigureAwait(false);
            if (!_IsCurrent(sentence))
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            try
            {
                await _prepared.Writer.WriteAsync(new PreparedSentence(sentence, audio), sentence.CancellationToken).ConfigureAwait(false);
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
            if (!_IsCurrent(sentence))
            {
                _Complete(sentence, played: false, aborted: true);
                continue;
            }

            _SetPlaybackActive(true, sentence.Source, sentence.OwnerId);
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
                await _audioPlayback.PlayAsync(pcmBytes, new AudioFormat(prepared.Audio.SampleRate, Channels: 1), sentence.CancellationToken)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _lastPlaybackEnded, Stopwatch.GetTimestamp());
                _Complete(sentence, played: true, aborted: false);
            }
            catch (OperationCanceledException)
            {
                _Complete(sentence, played: false, aborted: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Read-aloud playback failed ({ErrorType}) for a queued sentence; skipping it and continuing.", ex.GetType().Name);
                _Complete(sentence, played: false, aborted: false);
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

        _SetInactiveIfDrained();
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
            _logger.LogWarning("TTS synthesis failed ({ErrorType}) for a queued sentence; skipping it.", ex.GetType().Name);
            return null;
        }
    }

    private (int Generation, CancellationToken Token) _CaptureGeneration()
    {
        lock (_activeGate) { return (_generation, _playbackCancellation.Token); }
    }

    private bool _IsCurrent(QueuedSentence sentence)
    {
        lock (_activeGate)
        {
            return sentence.Generation == _generation && sentence.CancellationToken == _playbackCancellation.Token && !sentence.CancellationToken.IsCancellationRequested;
        }
    }

    private void _SetAudioPending(bool pending)
    {
        lock (_activeGate)
        {
            if (_audioPending == pending)
            {
                return;
            }
            _audioPending = pending;
            AudioPendingChanged?.Invoke(this, pending);
        }
    }

    private void _SetInactiveIfDrained()
    {
        lock (_activeGate)
        {
            if (Volatile.Read(ref _queuedSentenceCount) != 0)
            {
                return;
            }
            _SetAudioPending(false);
            if (_openStreams.Count != 0)
            {
                return;
            }
        }
        _SetPlaybackActive(false);
    }

    private void _SetPlaybackActive(bool active, VoicePlaybackSource source = VoicePlaybackSource.Session, string? ownerId = null)
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
                _activeOwnerId = ownerId;
            }
            else
            {
                _speakingAnnounced = false;
            }
        }

        PlaybackActiveChanged?.Invoke(this, active);
    }
}
