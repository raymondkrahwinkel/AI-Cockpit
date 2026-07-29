using Avalonia.Controls;
using Avalonia.Input;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

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
        // Started here, not awaited: the dialog has to be on screen (and its Cancel button clickable) while the
        // list loads (AC-502 criterion 6) rather than the window itself waiting on the network call to open.
        _ = viewModel.LoadAsync();
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
