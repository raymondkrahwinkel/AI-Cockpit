using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Views;

/// <summary>
/// The "Choose…" picker for a Memory row whose source can enumerate its own locations (AC-502). Returns the picked
/// location's bare value from <c>ShowDialog&lt;string?&gt;</c>, or null when the operator cancelled.
/// </summary>
public partial class MemorySourceLocationPickerDialog : Window
{
    // Review fix: DataContextChanged can in principle fire more than once (Screenshotter builds the view model,
    // loads it once synchronously for a deterministic render, then assigns it as DataContext, firing this a second
    // time). Without this guard each firing adds another CloseRequested subscriber and starts another concurrent
    // LoadAsync — one live subscriber and one starting load is what this window ever needs.
    private bool _wired;

    // AC-499: _ScrollToWholeRows needs a laid-out window to measure a row against, but DataContext is assigned
    // (firing OnDataContextChanged, which starts the load) before Show() is ever called at every call site this
    // dialog has — ProjectDialog's own picker included. When the listing delegate resolves synchronously (as
    // Screenshotter's scenes and this dialog's own tests do), LoadAsync's await never actually yields, so the scroll
    // attempt used to run to completion inside that same DataContext assignment — before the window was shown, so
    // nothing was realized to scroll against, and it silently did nothing. Bookkeeping both sides of the race
    // (Opened can fire before or after the load resolves, depending on how the listing delegate behaves) rather
    // than assuming an order neither call site guarantees.
    private bool _opened;
    private MemorySourceLocationPickerViewModel? _viewModelAwaitingScroll;

    public MemorySourceLocationPickerDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wired || DataContext is not MemorySourceLocationPickerViewModel viewModel)
        {
            return;
        }

        _wired = true;
        CockpitWindowChrome.Apply(this, "Choose a location");
        viewModel.CloseRequested += value => Close(value);
        Opened += (_, _) =>
        {
            _opened = true;
            _TryScrollToCurrent();
        };
        // Started here, not awaited: the dialog has to be on screen (and its Cancel button clickable) while the
        // list loads (AC-502 criterion 6) rather than the window itself waiting on the network call to open.
        _ = _LoadAsync(viewModel);
    }

    private async Task _LoadAsync(MemorySourceLocationPickerViewModel viewModel)
    {
        await viewModel.LoadAsync();
        _viewModelAwaitingScroll = viewModel;
        _TryScrollToCurrent();
    }

    // AC-499: once the list has settled *and* the window is actually on screen, scroll the pre-selected "current"
    // row into view rather than leaving the operator to find it themselves — the whole point of pre-selecting it is
    // that they see where they came from without hunting. A no-op when nothing matched (SelectedLocation stays
    // null; see the view model's own remarks on why no match means no selection, not a fallback pick) or when
    // either half of the race above has not happened yet — the other side re-enters this once it does.
    private void _TryScrollToCurrent()
    {
        if (!_opened || _viewModelAwaitingScroll is not { SelectedLocation: { } current } viewModel)
        {
            return;
        }

        _viewModelAwaitingScroll = null;
        _ScrollToWholeRows(viewModel, current);
    }

    /// <summary>
    /// Review fix (AC-499): the built-in <see cref="ListBox.ScrollIntoView"/> scrolls the minimum distance needed
    /// to bring the target row fully into view — which, whenever the list's own visible height is not an exact
    /// multiple of a row's height, stops with the row *before* it sliced in half at the opposite edge. That read
    /// as a data defect (a Detail line with no Name above it) rather than a scroll position. Rows are a uniform
    /// height now (see the item <c>DataTemplate</c>'s own remarks on <c>TargetNullValue</c>), so once that height
    /// is known the fix is arithmetic: cap the list's own visible area to a whole multiple of it, then only ever
    /// scroll in whole-row steps — which keeps both edges clean at any position, not only whichever edge the
    /// built-in method happened to land the target row against.
    /// </summary>
    private void _ScrollToWholeRows(MemorySourceLocationPickerViewModel viewModel, ProjectMemorySourceLocation current)
    {
        UpdateLayout();

        // Not the compiled x:Name field (LocationsList): measured live, the generated field is still null at the
        // point Show() raises Opened — Opened fires mid-way through building this window, before x:Name fields are
        // assigned, even though the visual tree underneath is already fully realized (GetVisualDescendants finds
        // 127 nodes, including this same ListBox, right then). A field that reads null on a tree that already
        // contains it is exactly the "the helper is itself a fault source" trap AvaloniaUI.md warns about — walking
        // the live tree here sidesteps it rather than caching a reference that might predate its own assignment.
        var listBox = this.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();
        if (listBox is null)
        {
            return;
        }

        var firstRow = listBox.ContainerFromIndex(0);
        var scrollViewer = listBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (firstRow is not { Bounds.Height: > 0 } || scrollViewer is null)
        {
            // Nothing realized to measure a row against — leaves the list at its default (top) scroll rather
            // than falling back to the built-in method, which is exactly the behaviour this exists to replace.
            return;
        }

        // Bounds is the item's own rendered box and excludes its Margin — Theme.axaml's global ListBoxItem style
        // sets Margin="0,1", so the distance one row actually advances the stack (what a scroll offset increments
        // against) is Bounds.Height plus that margin, not Bounds.Height alone. Missing it here made the maths
        // drift by 2px per row — 18px over nine rows, enough on its own to slice a row at the target's far edge.
        var rowHeight = firstRow.Bounds.Height + firstRow.Margin.Top + firstRow.Margin.Bottom;
        var wholeRows = (int)(scrollViewer.Viewport.Height / rowHeight);
        var locations = viewModel.Locations;
        if (wholeRows <= 0 || locations.Count <= wholeRows)
        {
            // Every row already fits inside the list's own height — nothing to scroll, nothing to cap.
            return;
        }

        // The only way an offset that is itself a multiple of the row height can land both the top and bottom
        // edge on a row boundary, at any scroll position: make the visible area exactly that multiple.
        listBox.Height = wholeRows * rowHeight;
        UpdateLayout();

        var index = locations.IndexOf(current);
        var topIndex = Math.Clamp(index - (wholeRows / 2), 0, locations.Count - wholeRows);
        scrollViewer.Offset = new Vector(0, topIndex * rowHeight);
    }

    // A quick pick for the common case — double-clicking a location is the same as selecting it and pressing
    // Choose, without an extra round trip through the button.
    private void OnLocationDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MemorySourceLocationPickerViewModel { SelectedLocation: not null } viewModel
            && viewModel.PickCommand.CanExecute(null))
        {
            viewModel.PickCommand.Execute(null);
        }
    }
}
