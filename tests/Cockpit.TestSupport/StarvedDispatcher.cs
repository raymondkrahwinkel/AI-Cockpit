using System.Diagnostics;
using Avalonia.Threading;

namespace Cockpit.TestSupport;

/// <summary>
/// A UI thread that never goes idle (AC-1200): a job that busies the dispatcher for a slice and then reposts itself
/// at the same priority, so everything queued below that priority never gets a turn.
/// </summary>
/// <remarks>
/// Written for AC-1138 and meant to be shared with AC-1196 and AC-1204 — three different fixes on three code paths,
/// one arrangement. Written once here rather than three times there.
/// <para>
/// Started above <see cref="DispatcherPriority.Default"/> — <c>Loaded</c> is 1, <c>Render</c> is 4, which is what
/// layout and render themselves run at — work posted at Default never runs while this spins, and the thread keeps
/// pumping meanwhile: starvation, not a blocked thread. Started at Default the queue interleaves instead, which is
/// the control showing that the priority is what does it.
/// </para>
/// <para>
/// A test that wants a genuinely blocked thread does not need this: one long non-yielding job posted at Default is
/// that case, and it is a different one — see AC-1138's T1 versus T2.
/// </para>
/// <para><see cref="Dispose"/> stops the reposting; whatever the dispatcher still holds then runs as usual.</para>
/// </remarks>
public sealed class StarvedDispatcher : IDisposable
{
    private readonly DispatcherPriority _priority;
    private readonly TimeSpan _slice;
    private volatile bool _stopped;
    private int _rounds;

    private StarvedDispatcher(DispatcherPriority priority, TimeSpan slice)
    {
        _priority = priority;
        _slice = slice;
    }

    /// <summary>How often the loop has run, which is what tells a starved thread from a stopped one.</summary>
    public int Rounds => Volatile.Read(ref _rounds);

    /// <summary>Queues the loop and returns at once; callable from any thread, including a starved one.</summary>
    public static StarvedDispatcher Start(DispatcherPriority priority, TimeSpan? slice = null)
    {
        var starver = new StarvedDispatcher(priority, slice ?? TimeSpan.FromMilliseconds(5));
        Dispatcher.UIThread.Post(starver._Spin, priority);
        return starver;
    }

    public void Dispose() => _stopped = true;

    private void _Spin()
    {
        if (_stopped)
        {
            return;
        }

        // Busy rather than asleep: a Task.Delay would hand the thread back and leave nothing to starve.
        var until = Stopwatch.GetTimestamp() + (long)(_slice.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < until)
        {
            Thread.SpinWait(20);
        }

        Interlocked.Increment(ref _rounds);
        Dispatcher.UIThread.Post(_Spin, _priority);
    }
}
