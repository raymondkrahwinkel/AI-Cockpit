using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Tracks call count/concurrency for <see cref="VoicePlaybackQueueTests"/> — the invariant under test is that the queue never calls this concurrently.</summary>
internal sealed class FakeAudioPlaybackService : IAudioPlaybackService
{
    private int _concurrentCalls;
    private int _callCount;

    public int CallCount => _callCount;

    public int MaxConcurrentCalls { get; private set; }

    /// <summary>Runs inside <see cref="PlayAsync"/>, given the caller's cancellation token — a test hook to simulate "still playing" or "cancelled mid-playback".</summary>
    public Func<CancellationToken, Task>? OnPlay { get; set; }

    public async Task PlayAsync(ReadOnlyMemory<byte> pcm, AudioFormat format, CancellationToken cancellationToken = default)
    {
        var concurrent = Interlocked.Increment(ref _concurrentCalls);
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, concurrent);
        Interlocked.Increment(ref _callCount);
        try
        {
            if (OnPlay is not null)
            {
                await OnPlay(cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCalls);
        }
    }
}
