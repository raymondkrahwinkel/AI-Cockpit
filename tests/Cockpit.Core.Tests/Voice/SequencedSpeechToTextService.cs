using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// ISpeechToTextService test double with no internal serialization of its own — unlike
/// <see cref="GatedSpeechToTextService"/>, a call here never waits on another call. Used to prove a claim
/// about event <em>order</em> without a shared gate obscuring which clip actually finished first.
/// </summary>
internal sealed class SequencedSpeechToTextService : ISpeechToTextService
{
    private int _callIndex = -1;

    /// <summary>Per-call hook, given the 0-based call index.</summary>
    public Func<int, CancellationToken, Task<string>>? OnTranscribe { get; set; }

    public Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _callIndex);
        return OnTranscribe is null ? Task.FromResult(string.Empty) : OnTranscribe(index, cancellationToken);
    }

    public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public event EventHandler<VoicePreparationProgress>? Preparing { add { } remove { } }

    public event EventHandler? Prepared { add { } remove { } }

    public WhisperRuntimeBackend? ActiveBackend => null;
}
