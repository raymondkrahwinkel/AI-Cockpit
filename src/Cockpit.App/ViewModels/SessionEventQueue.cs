using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Marshals session events onto the UI thread in batches rather than one post per event (AC-529).
//
// The runtime pumps its driver off the UI thread and raises an event per streamed delta, which used to be one
// `Dispatcher.UIThread.Post` each. Here the producer only enqueues, and asks for a drain when no drain is
// already pending — so a quiet stream still gets a post per event and loses no responsiveness, while a burst the UI
// thread cannot keep up with piles into the queue and is applied by the one drain already on its way.
//
// It is self-clocking on purpose: no timer, no interval, and therefore no buffer that is holding events on a
// deadline. That is what keeps the "nothing is left behind at the end of a turn, on an error, or when the pane
// closes" property cheap — every enqueue either finds a drain pending or posts one, so as long as the dispatcher
// still runs a posted action, the queue empties.
//
// An event whose apply throws costs only itself. Batching must not widen the blast radius of a bad event: with one
// post per event a throw took down that event alone and its neighbours ran in their own untouched posts, so the
// drain keeps applying the rest of the batch and re-posts the failure instead of carrying it out of the loop. It is
// re-thrown, never swallowed — on its own posted action, so it reaches the same global handler, with the same
// original stack, that a per-event post used to reach. What "itself" covers is one *applied* event, which
// after folding is a run of adjacent deltas of one row within one batch rather than a single delta: narrower than
// the batch, wider than the one delta the old path would have lost.
internal sealed class SessionEventQueue
{
    private readonly Action<SessionEvent> _apply;
    private readonly Action<Action> _post;
    private readonly ConcurrentQueue<SessionEvent> _pending = new();
    private int _drainPending;

    // `apply`: Applies one event to the view model; runs on whatever thread `post` lands on.
    // `post`:
    // Test seam, the same shape as `ToastHostViewModel`'s scheduler and `DevPluginReloadWatcher`'s
    // debounce: null takes the real `Dispatcher.UIThread`, a test hands in a manual pump so the drain
    // runs when it says so instead of when a real dispatcher gets round to it.
    public SessionEventQueue(Action<SessionEvent> apply, Action<Action>? post = null)
    {
        _apply = apply;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    // Takes one event from the runtime's pump thread and makes sure a drain is coming.
    public void Enqueue(SessionEvent evt)
    {
        _pending.Enqueue(evt);
        if (Interlocked.Exchange(ref _drainPending, 1) == 0)
        {
            _post(Drain);
        }
    }

    // Applies everything queued so far, in arrival order, with adjacent deltas folded. In the app it only ever runs
    // as the action `Enqueue` posted; a test's manual pump runs the same method.
    internal void Drain()
    {
        // Cleared before the queue is read, never after. An event enqueued while this drain runs then finds the flag
        // clear and posts its own drain, so the worst case is a later drain that finds nothing to do — whereas
        // clearing afterwards would let an event slip in between the last dequeue and the clear with no drain coming
        // for it, which is silent loss.
        Interlocked.Exchange(ref _drainPending, 0);

        // A fresh list per drain rather than a reused field: Coalesce hands this very instance back when a batch had
        // nothing to fold, so a reused buffer would be cleared underneath an iteration if a drain ever nested.
        var batch = new List<SessionEvent>();
        while (_pending.TryDequeue(out var evt))
        {
            batch.Add(evt);
        }

        if (batch.Count == 0)
        {
            return;
        }

        foreach (var evt in SessionEventCoalescer.Coalesce(batch))
        {
            try
            {
                _apply(evt);
            }
            catch (Exception ex)
            {
                // Re-posted rather than re-thrown here: thrown from this loop it would take the rest of the batch
                // with it, which is exactly the blast radius one-post-per-event did not have. Capture/Throw keeps the
                // original stack, so the global handler sees what it saw before.
                var failure = ExceptionDispatchInfo.Capture(ex);
                _post(failure.Throw);
            }
        }
    }
}
