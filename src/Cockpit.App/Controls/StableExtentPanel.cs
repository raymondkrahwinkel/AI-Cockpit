using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;

namespace Cockpit.App.Controls;

// AC-1265: a virtualising panel whose extent never shrinks under the reader, because a shrinking one makes
// the ScrollViewer clamp the offset with it — 113209px of backwards jump in a 1500-row transcript.
internal sealed class StableExtentPanel : VirtualizingPanel
{
    // Only used until the first row has been measured; after that the average of what we know is closer.
    private const double InitialRowHeight = 48;

    // Heights are kept against the item, not its index, so filtering the transcript by reading level or
    // inserting a row does not shift every cached height by one. Weakly, because a panel that outlives a
    // removed row must not be the reason its view model stays alive.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, StrongBox<double>> _heights = new();
    private readonly Dictionary<int, Control> _realised = [];

    // Running sum of row heights, rebuilt only when something changed, so a measure costs a binary search
    // rather than a walk over every row.
    private readonly List<double> _starts = [];

    // The mean height over every row measured so far, kept as a sum and a count because a weak table cannot
    // be counted. A rolling average was tried and is wrong: the tail is re-measured on every streamed token,
    // so its height drags the mean towards itself and the estimate for unseen rows runs away with it.
    private double _heightSum;
    private int _heightCount;
    private double _measuredWidth = double.NaN;
    private bool _startsDirty = true;
    private double _measuredFrom;
    private double _measuredTo;
    private ScrollViewer? _scroller;

    // A transcript shorter than the viewport asks for less height than it is given, and the presenter then
    // centres it — measured at 128px down the page for a single row. It belongs at the top.
    public StableExtentPanel() => VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is not ScrollViewer scroller)
            {
                continue;
            }

            _scroller = scroller;
            scroller.PropertyChanged += _OnScrollerChanged;
            break;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroller is { } scroller)
        {
            scroller.PropertyChanged -= _OnScrollerChanged;
            _scroller = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // Only a move that leaves the band we last measured needs a new measure; re-measuring on every pixel
    // of scroll is a layout pass per arriving row, which is the cost AC-1111 bought back.
    private void _OnScrollerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.OffsetProperty && e.Property != ScrollViewer.ViewportProperty)
        {
            return;
        }

        if (_scroller is not { } scroller)
        {
            return;
        }

        var top = scroller.Offset.Y;
        if (top < _measuredFrom || top + scroller.Viewport.Height > _measuredTo)
        {
            InvalidateMeasure();
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);

        // Never drop every container here. Doing so leaves the panel empty for one pass, the ScrollViewer
        // clamps the offset to zero, and the reader is thrown to the top of the transcript and back on every
        // arriving row — measured as 40 of 88 painted frames with the reply nowhere on screen.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                // A reset says nothing about which items survived, so the only safe reading is that none did.
                _heights.Clear();
                _heightSum = 0;
                _heightCount = 0;
                _RecycleOutside(0, -1);
                break;

            case NotifyCollectionChangedAction.Add:
                _ShiftRealised(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;

            case NotifyCollectionChangedAction.Remove:
                _RecycleRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                _ShiftRealised(e.OldStartingIndex, -(e.OldItems?.Count ?? 0));
                break;

            case NotifyCollectionChangedAction.Replace:
                _RecycleRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;

            case NotifyCollectionChangedAction.Move:
                _RecycleOutside(0, -1);
                break;
        }

        _startsDirty = true;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        if (items.Count == 0)
        {
            _RecycleOutside(0, -1);
            return default;
        }

        // A row's height is only valid for the width it was measured at, so a resize invalidates all of it.
        if (!double.IsNaN(_measuredWidth) && Math.Abs(_measuredWidth - availableSize.Width) > 0.5)
        {
            _heights.Clear();
            _heightSum = 0;
            _heightCount = 0;
            _startsDirty = true;
        }

        _measuredWidth = availableSize.Width;
        _RebuildStarts();
        var top = Math.Max(0, _scroller?.Offset.Y ?? 0);
        var height = _ViewportHeight(availableSize);
        var first = _IndexAt(top);

        // How far to keep going is decided by what the rows actually measure, not by what they were
        // estimated at: a row estimated at the fallback but measuring nothing would otherwise leave a gap
        // the size of that estimate, which is how the first version pushed a short transcript down the page.
        var last = first;
        var filled = _StartOf(first) - top;
        for (var index = first; index < items.Count; index++)
        {
            var container = _Realise(index);
            container.Measure(new Size(availableSize.Width, double.PositiveInfinity));

            var measured = container.DesiredSize.Height;
            if (items[index] is { } item && _Remember(item, measured))
            {
                _startsDirty = true;
            }

            last = index;
            filled += measured;
            if (filled >= height)
            {
                break;
            }
        }

        _RecycleOutside(first, last);
        _RebuildStarts();

        _measuredFrom = _StartOf(first);
        _measuredTo = _StartOf(last) + _HeightOf(last);
        return new Size(availableSize.Width, _Total());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _RebuildStarts();

        foreach (var (index, container) in _realised)
        {
            container.Arrange(new Rect(0, _StartOf(index), finalSize.Width, _HeightOf(index)));
        }

        return new Size(finalSize.Width, _Total());
    }

    private double _ViewportHeight(Size availableSize)
    {
        if (_scroller is { Viewport.Height: > 0 } scroller)
        {
            return scroller.Viewport.Height;
        }

        return double.IsInfinity(availableSize.Height) || availableSize.Height <= 0 ? InitialRowHeight * 12 : availableSize.Height;
    }

    private double _HeightOf(int index) =>
        index >= 0 && index < Items.Count && Items[index] is { } item && _heights.TryGetValue(item, out var h) ? h.Value : _Fallback;

    // Records a row's measured height and folds it into the estimate. True when the total moved.
    private bool _Remember(object item, double measured)
    {
        if (_heights.TryGetValue(item, out var known))
        {
            if (Math.Abs(known.Value - measured) <= 0.5)
            {
                return false;
            }

            _heightSum += measured - known.Value;
            known.Value = measured;
            return true;
        }

        _heights.Add(item, new StrongBox<double>(measured));
        _heightSum += measured;
        _heightCount++;
        return true;
    }

    private double _Fallback => _heightCount > 0 ? _heightSum / _heightCount : InitialRowHeight;

    private void _RebuildStarts()
    {
        if (!_startsDirty)
        {
            return;
        }

        var items = Items;
        _starts.Clear();
        _starts.EnsureCapacity(items.Count + 1);

        var y = 0.0;
        for (var index = 0; index < items.Count; index++)
        {
            _starts.Add(y);
            y += items[index] is { } item && _heights.TryGetValue(item, out var h) ? h.Value : _Fallback;
        }

        _starts.Add(y);
        _startsDirty = false;
    }

    private double _Total() => _starts.Count > 0 ? _starts[^1] : 0;

    private double _StartOf(int index) => index >= 0 && index < _starts.Count ? _starts[index] : 0;

    // The row covering `offset`, found by binary search over the running sum.
    private int _IndexAt(double offset)
    {
        if (Items.Count == 0)
        {
            return 0;
        }

        var found = _starts.BinarySearch(offset);
        var index = found >= 0 ? found : Math.Max(0, ~found - 1);
        return Math.Clamp(index, 0, Items.Count - 1);
    }

    private Control _Realise(int index)
    {
        if (_realised.TryGetValue(index, out var existing))
        {
            return existing;
        }

        var item = Items[index];
        var generator = ItemContainerGenerator
            ?? throw new InvalidOperationException("A virtualising panel outside an ItemsControl cannot make containers.");

        Control container;
        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            container = generator.CreateContainer(item, index, recycleKey);
            generator.PrepareItemContainer(container, item, index);
        }
        else if (item is Control itemAsContainer)
        {
            container = itemAsContainer;
        }
        else
        {
            throw new InvalidOperationException($"Item {index} needs no container but is not one either.");
        }

        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        _realised[index] = container;
        return container;
    }

    // Rows arriving before a realised one move it along; the generator is told so its own bookkeeping follows.
    private void _ShiftRealised(int from, int delta)
    {
        if (delta == 0)
        {
            return;
        }

        var moved = _realised.Where(pair => pair.Key >= from).OrderBy(pair => delta > 0 ? -pair.Key : pair.Key).ToList();
        foreach (var (index, container) in moved)
        {
            _realised.Remove(index);
            _realised[index + delta] = container;
            ItemContainerGenerator?.ItemContainerIndexChanged(container, index, index + delta);
        }
    }

    private void _RecycleRange(int from, int count)
    {
        for (var index = from; index < from + count; index++)
        {
            if (!_realised.TryGetValue(index, out var container))
            {
                continue;
            }

            _realised.Remove(index);
            ItemContainerGenerator?.ClearItemContainer(container);
            RemoveInternalChild(container);
        }
    }

    private void _RecycleOutside(int keepFrom, int keepTo)
    {
        foreach (var index in _realised.Keys.ToList())
        {
            if (index >= keepFrom && index <= keepTo)
            {
                continue;
            }

            var container = _realised[index];
            _realised.Remove(index);
            ItemContainerGenerator?.ClearItemContainer(container);
            RemoveInternalChild(container);
        }
    }

    // Bringing a row into view is exact here rather than approximate: its offset is in the running sum even
    // when the row itself has never been realised.
    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
        {
            return null;
        }

        _RebuildStarts();

        if (_scroller is { } scroller)
        {
            var start = _StartOf(index);
            var end = start + _HeightOf(index);
            var top = scroller.Offset.Y;
            var bottom = top + scroller.Viewport.Height;

            if (start < top)
            {
                scroller.Offset = scroller.Offset.WithY(start);
            }
            else if (end > bottom)
            {
                scroller.Offset = scroller.Offset.WithY(Math.Max(0, end - scroller.Viewport.Height));
            }
        }

        return _realised.GetValueOrDefault(index);
    }

    protected override Control? ContainerFromIndex(int index) => _realised.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container)
    {
        foreach (var (index, realised) in _realised)
        {
            if (ReferenceEquals(realised, container))
            {
                return index;
            }
        }

        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() => _realised.Values;

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        if (Items.Count == 0)
        {
            return null;
        }

        var current = from is Control control ? IndexFromContainer(control) : -1;
        var next = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => Items.Count - 1,
            NavigationDirection.Next or NavigationDirection.Down => current + 1,
            NavigationDirection.Previous or NavigationDirection.Up => current - 1,
            _ => -1,
        };

        if (next < 0 || next >= Items.Count)
        {
            return wrap && Items.Count > 0 ? ScrollIntoView(next < 0 ? Items.Count - 1 : 0) : null;
        }

        return ScrollIntoView(next);
    }
}
