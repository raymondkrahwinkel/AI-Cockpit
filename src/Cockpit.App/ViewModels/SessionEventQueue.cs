using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewModels;

// Marshals session events onto the UI thread in batches rather than one post per event (AC-529).
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

    // `post`/`postAfterWindow`: Test seams, the same shape as `ToastHostViewModel`'s scheduler and
    // `DevPluginReloadWatcher`'s debounce: null takes the real dispatcher, a test hands in a manual pump so each drain
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

    // Applies what the drain window is still holding.
    public void Flush()
    {
        _ApplyQueued();
        Interlocked.Exchange(ref _drainPending, 0);
    }

    // Whether a flush would do anything. A pane that closes off the UI thread posts its flush, and a posted action
    // holds the pane until the dispatcher gets to it — worth not asking for when there is nothing to apply and no
    // flag to clear (AC-787).
    public bool HasWork => !_pending.IsEmpty || Volatile.Read(ref _drainPending) != 0;

    // Takes one event from the runtime's pump thread and makes sure a drain is coming.
    public void Enqueue(SessionEvent evt)
    {
        _pending.Enqueue(evt);
        if (Interlocked.Exchange(ref _drainPending, 1) == 0)
        {
            _post(Drain);
        }
    }

    // Now that the flag is only released at the end of a burst, an escape that skipped the release would latch it at 1
    // for good and the pane would go silently deaf to its runtime — silently, because a throw on the UI thread is
    // marked handled and the app keeps running (Program.cs).
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
                // Re-posted rather than re-thrown here: thrown from this loop it would take the rest of the batch with
                // it, which is exactly the blast radius one-post-per-event did not have.
                var failure = ExceptionDispatchInfo.Capture(ex);
                _post(failure.Throw);
            }
        }

        return true;
    }
}
