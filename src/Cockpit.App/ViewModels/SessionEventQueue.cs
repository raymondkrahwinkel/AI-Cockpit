using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Marshals session events onto the UI thread in batches rather than one post per event (AC-529).
//
// The runtime pumps its driver off the UI thread and raises an event per streamed delta, which used to be one
// `Dispatcher.UIThread.Post` each. Here the producer only enqueues, and asks for a drain when no drain is
// already pending, so a burst piles into the queue and is applied by the one drain already on its way.
//
// AC-529: the first event after a quiet moment is applied at once and the ones behind it are folded into one drain
// per frame, because posting each drain immediately was measured on a live session to fold nothing at all.
//
// AC-529: that window means events can wait on a deadline, so "nothing is left behind at the end of a turn, on an
// error, or when the pane closes" is no longer free. `Flush` buys it back.
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
    // One frame at 30 fps, matching `MarkdownView.RebuildIntervalMs`: the markdown rows repaint on that cadence
    // anyway, so folding the deltas that arrive between two repaints removes work no one could have seen.
    private const int DrainDelayMs = 33;

    private readonly Action<SessionEvent> _apply;
    private readonly Action<Action> _post;
    private readonly Action<Action> _postAfterWindow;
    private readonly ConcurrentQueue<SessionEvent> _pending = new();
    private int _drainPending;

    // `apply`: Applies one event to the view model; runs on whatever thread the posts land on.
    // `post`/`postAfterWindow`:
    // Test seams, the same shape as `ToastHostViewModel`'s scheduler and `DevPluginReloadWatcher`'s
    // debounce: null takes the real dispatcher, a test hands in a manual pump so each drain
    // runs when it says so instead of when a real dispatcher gets round to it.
    public SessionEventQueue(
        Action<SessionEvent> apply,
        Action<Action>? post = null,
        Action<Action>? postAfterWindow = null)
    {
        _apply = apply;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));

        // Posted first, timer armed second: `DispatcherTimer` binds to `Dispatcher.CurrentDispatcher`, so arming one
        // straight from the runtime's pump thread lands it on a dispatcher nothing pumps (AC-529, and the shape
        // `TtyViewModel` already uses). One-shot, so nothing keeps ticking against a recycled pane (AC-611).
        _postAfterWindow = postAfterWindow
            ?? post
            ?? (action => Dispatcher.UIThread.Post(
                () => DispatcherTimer.RunOnce(action, TimeSpan.FromMilliseconds(DrainDelayMs))));
    }

    // Applies what the drain window is still holding. Runs `_apply` inline, so the caller owes it the UI thread.
    // Releases the flag as part of flushing: a flush leaves nothing for a window to find, so leaving "a drain is
    // coming" standing would mean the next event posts nothing — and a pane that clears its context reuses this
    // very queue for the runtime that follows.
    public void Flush()
    {
        _ApplyQueued();
        Interlocked.Exchange(ref _drainPending, 0);
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

    // The leading edge: applies what is queued now, then holds the window open so the events behind it fold.
    //
    // Anything that escapes here releases the flag on its way out. The version this replaces cleared the flag as its
    // first statement, so a throw cost one batch and the next event started a fresh drain. Now that the flag is only
    // released at the end of a burst, an escape that skipped the release would latch it at 1 for good and the pane
    // would go silently deaf to its runtime — silently, because a throw on the UI thread is marked handled and the
    // app keeps running (Program.cs).
    internal void Drain()
    {
        try
        {
            _ApplyQueued();
            _postAfterWindow(_DrainWindow);
        }
        catch
        {
            _EndBurst();
            throw;
        }
    }

    // One folded drain per window, for as long as events keep arriving. A window that finds nothing ends the burst,
    // so the next event gets its own leading edge instead of waiting a frame for nothing.
    private void _DrainWindow()
    {
        try
        {
            if (_ApplyQueued())
            {
                _postAfterWindow(_DrainWindow);
                return;
            }
        }
        catch
        {
            _EndBurst();
            throw;
        }

        _EndBurst();
    }

    // Releases the flag, then looks once more. In that order, never the other way round: an event that slips in behind
    // the release finds the flag clear and posts its own drain, whereas releasing afterwards would leave it queued
    // with nothing coming for it.
    private void _EndBurst()
    {
        Interlocked.Exchange(ref _drainPending, 0);

        if (!_pending.IsEmpty && Interlocked.Exchange(ref _drainPending, 1) == 0)
        {
            _post(Drain);
        }
    }

    // Applies everything queued so far, in arrival order, with adjacent deltas folded. Returns whether it found any.
    private bool _ApplyQueued()
    {
        // A fresh list per drain rather than a reused field: Coalesce hands this very instance back when a batch had
        // nothing to fold, so a reused buffer would be cleared underneath an iteration if a drain ever nested.
        var batch = new List<SessionEvent>();
        while (_pending.TryDequeue(out var evt))
        {
            batch.Add(evt);
        }

        if (batch.Count == 0)
        {
            return false;
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
                // original stack, so the global handler sees what it saw before. Through `_post`, never the window:
                // a timer-armed throw reaches no unhandled-exception handler at all.
                var failure = ExceptionDispatchInfo.Capture(ex);
                _post(failure.Throw);
            }
        }

        return true;
    }
}
