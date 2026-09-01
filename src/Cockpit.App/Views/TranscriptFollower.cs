using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-1121: the transcript's stick-to-bottom, once, for SessionView and AssistantChatView both. It existed twice
// and every repair since has landed on one half (AC-953, AC-996, AC-378).
internal sealed class TranscriptFollower
{
    // AC-1178: a follow driven from ScrollChanged invalidated inside the frame it was ending, and the next pass
    // counted towards Avalonia's cut-off at 153. Loaded sits outside the render callbacks, so a step here starts
    // a new frame with a fresh count.
    private static readonly DispatcherPriority FollowPriority = DispatcherPriority.Loaded;

    // Loaded is above Default, and work above Default that reposts itself starves everything below it completely
    // (AC-1200: the MCP hop never got in over 8s). No step ever posts the next one — the continuation comes from
    // the scroll change our own move raises — and one tail gets at most this many before yielding to Background.
    private const int MaxSteps = 8;

    private readonly ItemsControl _items;
    private readonly Func<ScrollViewer?> _scroll;

    private bool _stickToBottom = true;
    private bool _pointerHeld;
    private bool _wheelTurned;
    private int _wheelGeneration;

    private SessionViewModel? _watched;
    private TranscriptEntryViewModel? _watchedTail;

    private bool _posted;
    private int _steps;
    private bool _wasOnScreen;

    internal TranscriptFollower(ItemsControl items, Func<ScrollViewer?> scroll)
    {
        _items = items;
        _scroll = scroll;

        // The one edge the data cannot announce: a rail tile scrolling back into view has no new row to follow,
        // so without this it stays wherever the gate below left it until its session next says something.
        _items.EffectiveViewportChanged += (_, _) =>
        {
            var onScreen = _IsOnScreen(_items);
            if (onScreen && !_wasOnScreen)
            {
                RequestFollow();
            }

            _wasOnScreen = onScreen;
        };
    }

    // Whether the operator is parked at the tail. Settable because three callers move the viewport on purpose and
    // must not be followed straight back: AC-935's jump to a citation, AC-953's handover, AC-545's consent row.
    internal bool StickToBottom
    {
        get => _stickToBottom;
        set => _stickToBottom = value;
    }

    // True while a step is moving the viewport, so the ScrollChanged it drives is never read as the operator's.
    internal bool Following { get; private set; }

    // AC-1121: the cue comes from the data, not from the layout. A row arriving is a collection change; a row
    // growing while it streams is that row's own Text change, and nothing else announces that at all.
    internal void Watch(SessionViewModel? session)
    {
        if (ReferenceEquals(_watched, session))
        {
            return;
        }

        if (_watched is { } previous)
        {
            previous.VisibleTranscript.CollectionChanged -= _OnRowsChanged;
        }

        _watched = session;
        if (session is not null)
        {
            session.VisibleTranscript.CollectionChanged += _OnRowsChanged;
        }

        _WatchTail();
    }

    private void _OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _WatchTail();
        RequestFollow();
    }

    // Only the last row can be the streaming one, so only it is watched — subscribing to every row would hold a
    // handler per transcript entry for a signal that can only ever come from the tail.
    private void _WatchTail()
    {
        var tail = _watched?.VisibleTranscript is { Count: > 0 } rows ? rows[^1] : null;
        if (ReferenceEquals(_watchedTail, tail))
        {
            return;
        }

        if (_watchedTail is { } previous)
        {
            previous.PropertyChanged -= _OnTailChanged;
        }

        _watchedTail = tail;
        if (tail is not null)
        {
            tail.PropertyChanged += _OnTailChanged;
        }
    }

    private void _OnTailChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TranscriptEntryViewModel.Text))
        {
            // A token continues this tail's sequence; after its Loaded budget, following yields to Background.
            _Request(fresh: false);
        }
    }

    // Asks for the tail to be brought back into view: one post, and a fresh budget of steps to reach it.
    internal void RequestFollow() => _Request(fresh: true);

    private void _Request(bool fresh)
    {
        if (fresh)
        {
            _steps = 0;
        }

        if (_posted)
        {
            return;
        }

        _posted = true;
        Dispatcher.UIThread.Post(_Step, _steps < MaxSteps ? FollowPriority : DispatcherPriority.Background);
    }

    // A scroll change is never followed from here: Avalonia raises it from LayoutUpdated, so doing the move here
    // put a nested layout pass inside the pass that raised it — AC-1178 caught that chain in three live stacks.
    // It still carries two facts nothing else announces, and both are answered with a post.
    internal void NoteScrollChanged(double viewportDelta)
    {
        var byOperator = _wheelTurned || _pointerHeld;
        _wheelTurned = false;

        if (_scroll() is not { } scroll)
        {
            return;
        }

        if (byOperator)
        {
            // Only the operator can stop the follow and only the operator can resume it. Where they ended up is
            // the same fact after a wheel turn as after a scrollbar drag.
            _stickToBottom = NewestRowIsFullyVisible()
                || TranscriptScrollAnchor.IsAtBottom(scroll.Offset.Y, scroll.Extent.Height, scroll.Viewport.Height);
            return;
        }

        // AC-459: bands dock above the transcript and take their band out of it, and no data change says so.
        // AC-528: the estimated end lands short of the true bottom, and only the realised row's geometry closes
        // the rest — which needs the arrange this very change ends.
        if (Math.Abs(viewportDelta) >= 0.5)
        {
            _Request(fresh: true);
        }
        else if (!NewestRowIsFullyVisible())
        {
            _Request(fresh: false);
        }
    }

    // A wheel turn at the bottom scrolls nothing, so no ScrollChanged comes to consume the flag; it expires after
    // this turn's layout instead (AC-621). Bound to a generation because the unconditional post let the first of
    // two turns clear the second's flag — AC-621's own bug, in the fix for it.
    internal void NoteWheel()
    {
        _wheelTurned = true;
        var generation = ++_wheelGeneration;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_wheelGeneration == generation)
                {
                    _wheelTurned = false;
                }
            },
            DispatcherPriority.Background);
    }

    // Level-triggered where the wheel is edge-triggered: merging the two would let the wheel's expiry clear a
    // button that is still down, which is AC-621 again in a new form.
    internal void NotePointerHeld(bool held) => _pointerHeld = held;

    // Whether the newest row is on screen in full — the row's own bottom edge, not Extent, which is an estimate
    // that (measured, AC-528) sat ~300px above a reachable bottom.
    internal bool NewestRowIsFullyVisible()
    {
        // The last row the reading level actually shows — following one it hides could never terminate (AC-800).
        var newestIndex = _items.ItemCount - 1;
        if (newestIndex < 0)
        {
            return true;
        }

        if (_scroll() is not { } scroll)
        {
            return true;
        }

        // Not realised means below the viewport, which is the point of virtualisation. Above it cannot be: it is
        // the last row.
        if (_items.ContainerFromIndex(newestIndex) is not { } newest)
        {
            return false;
        }

        var bottom = newest.TranslatePoint(new Point(0, newest.Bounds.Height), scroll);
        // A pixel of slack for layout rounding, in the same spirit as TranscriptScrollAnchor's tolerance.
        return bottom is not null && bottom.Value.Y <= scroll.Viewport.Height + 1.0;
    }

    // AC-1178: the follow does not run on a tile nobody can see. A rail tile scrolled out of the rail keeps
    // IsVisible true and keeps being laid out — SessionTilePanel clips it — and following that one is what
    // reached the 153. Answered from geometry rather than from the render scene so it holds headless too.
    private static bool _IsOnScreen(Visual visual)
    {
        if (!visual.IsEffectivelyVisible)
        {
            return false;
        }

        foreach (var ancestor in visual.GetVisualAncestors().OfType<Visual>())
        {
            if (!ancestor.ClipToBounds)
            {
                continue;
            }

            if (visual.TranslatePoint(default, ancestor) is not { } topLeft
                || !new Rect(topLeft, visual.Bounds.Size).Intersects(new Rect(ancestor.Bounds.Size)))
            {
                return false;
            }
        }

        return true;
    }

    private void _Step()
    {
        _posted = false;

        if (!_stickToBottom || _scroll() is not { } scroll || !_IsOnScreen(_items))
        {
            return;
        }

        if (_steps < MaxSteps)
        {
            _steps++;
        }

        _MoveOneStep(scroll);
    }

    // One move per post. The old code tried three times in a row, but ContainerFromIndex and Bounds.Height only
    // settle after the arrange the first attempt queued, so attempts two and three read attempt one's numbers.
    private void _MoveOneStep(ScrollViewer scroll)
    {
        var newestIndex = _items.ItemCount - 1;
        if (newestIndex < 0 || NewestRowIsFullyVisible())
        {
            return;
        }

        Following = true;
        try
        {
            // Reach the current scrollbar end before using realised geometry to close any estimate residue.
            // Assigning Offset only invalidates; ScrollIntoView ran a whole nested layout pass here instead,
            // once per streamed row — 6.8MB and tens of milliseconds each, which is AC-1111 (measured).
            var reachableEnd = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            if (!TranscriptScrollAnchor.IsSettled(scroll.Offset.Y, reachableEnd))
            {
                _MoveTo(scroll, reachableEnd);
                return;
            }

            if (_items.ContainerFromIndex(newestIndex) is not { } newest
                || newest.TranslatePoint(new Point(0, newest.Bounds.Height), scroll) is not { } bottom)
            {
                return;
            }

            // Clamped to the end the viewer can actually reach: a row taller than the viewport leaves a shortfall
            // no offset can close, and a write past the end never settles, so the scroll change it raises asks for
            // another step forever — the loop that pins the UI thread while the tile visibly bounces.
            var shortfall = bottom.Y - scroll.Viewport.Height;
            if (shortfall > 0)
            {
                _MoveTo(scroll, Math.Min(scroll.Offset.Y + shortfall, reachableEnd));
            }
        }
        finally
        {
            Following = false;
        }
    }

    // AC-1113: a write that lands where the viewport already sits still invalidates layout, and every such pass
    // counts towards Avalonia's cut-off.
    private static void _MoveTo(ScrollViewer scroll, double offsetY)
    {
        if (!TranscriptScrollAnchor.IsSettled(scroll.Offset.Y, offsetY))
        {
            scroll.Offset = scroll.Offset.WithY(offsetY);
        }
    }
}
