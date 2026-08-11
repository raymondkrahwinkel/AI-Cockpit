using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// ISpeechToTextService test double that gates <see cref="TranscribeAsync"/> one clip at a time, the way
/// WhisperWorkerSpeechToTextService's own `_gate` does — proves a caller that fires clips without awaiting
/// them (AC-707) still never gets two transcriptions running at once.
/// </summary>
internal sealed class GatedSpeechToTextService : ISpeechToTextService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _concurrentCalls;

    public int MaxConcurrentCalls { get; private set; }

    public List<string> Transcribed { get; } = [];

    /// <summary>Per-call hook, given the 0-based call index — a test hook to hold a clip open to simulate "still transcribing".</summary>
    public Func<int, CancellationToken, Task<string>>? OnTranscribe { get; set; }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var concurrent = Interlocked.Increment(ref _concurrentCalls);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, concurrent);

            var index = Transcribed.Count;
            var text = OnTranscribe is null ? string.Empty : await OnTranscribe(index, cancellationToken).ConfigureAwait(false);
            Transcribed.Add(text);
            return text;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCalls);
            _gate.Release();
        }
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public event EventHandler<VoicePreparationProgress>? Preparing { add { } remove { } }

    public event EventHandler? Prepared { add { } remove { } }

    public WhisperRuntimeBackend? ActiveBackend => null;
}
