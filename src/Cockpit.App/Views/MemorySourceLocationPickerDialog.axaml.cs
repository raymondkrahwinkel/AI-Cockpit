using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.Views;

// The "Choose…" picker for a Memory row whose source can enumerate its own locations (AC-502). Returns the picked
// location's bare value from `ShowDialog&lt;string?&gt;`, or null when the operator cancelled.
public partial class MemorySourceLocationPickerDialog : Window
{
    // Review fix: DataContextChanged can fire more than once (Screenshotter re-assigns DataContext
    // after a synchronous preload); without this guard each firing would add another
    // CloseRequested subscriber and start another concurrent LoadAsync.
    private bool _wired;

    // AC-499: a synchronous listing delegate can resolve the scroll before the window is shown
    // (DataContext, and the load it starts, is always assigned before Show()) and silently do
    // nothing — so both sides of this race are tracked instead of assumed.
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

    // AC-499: once settled and on screen, scroll the pre-selected "current" row into view so the
    // operator sees where they came from without hunting. No-op when nothing matched or either
    // half of the race above hasn't happened yet — the other side re-enters this once it does.
    private void _TryScrollToCurrent()
    {
        if (!_opened || _viewModelAwaitingScroll is not { SelectedLocation: { } current } viewModel)
        {
            return;
        }

        _viewModelAwaitingScroll = null;
        _ScrollToWholeRows(viewModel, current);
    }

    // Review fix (AC-499): ScrollIntoView's minimum-distance scroll slices the row before the
    // target in half when the visible height isn't a whole row multiple — read as a data defect.
    // Fix: cap the visible area to a whole multiple of row height, scroll in whole-row steps.
    private void _ScrollToWholeRows(MemorySourceLocationPickerViewModel viewModel, ProjectMemorySourceLocation current)
    {
        UpdateLayout();

        // Not the compiled x:Name field: it's still null when Opened fires (assigned later in
        // construction) even though the live tree is already fully realized — the classic
        // "helper is itself a fault source" trap, so this walks the live tree instead.
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

        // Bounds excludes Margin (Theme.axaml sets Margin="0,1"), so the actual per-row advance is
        // Bounds.Height plus margin — missing it drifted the maths 2px/row, 18px over nine rows.
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
