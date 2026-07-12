using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The command palette dialog (#: command palette). The search box keeps focus so ↑/↓ move the selection and
/// Enter runs it (VS-Code style); a double-click (or Enter) activates a command, then the dialog closes and the
/// host runs the chosen command's action.
/// </summary>
public partial class CommandPaletteDialog : Window
{
    public CommandPaletteDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => QueryBox.Focus();

        // Tunnel so the arrow keys reach us before the TextBox consumes them (a bubbling handler never sees
        // Up/Down). A single click on a row runs it — selection lands on press, so it is correct by release.
        QueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        CommandList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is CommandPaletteDialogViewModel viewModel)
        {
            viewModel.CloseRequested += Close;
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandPaletteDialogViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                viewModel.RunCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                viewModel.CancelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                viewModel.Move(1);
                _ScrollToSelected(viewModel);
                e.Handled = true;
                break;
            case Key.Up:
                viewModel.Move(-1);
                _ScrollToSelected(viewModel);
                e.Handled = true;
                break;
        }
    }

    private void _ScrollToSelected(CommandPaletteDialogViewModel viewModel)
    {
        if (viewModel.Selected is { } selected)
        {
            CommandList.ScrollIntoView(selected);
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is CommandPaletteDialogViewModel { Selected: not null } viewModel)
        {
            viewModel.RunCommand.Execute(null);
        }
    }
}
