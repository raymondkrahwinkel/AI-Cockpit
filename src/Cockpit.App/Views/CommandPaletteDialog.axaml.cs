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
                e.Handled = true;
                break;
            case Key.Up:
                viewModel.Move(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnItemActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CommandPaletteDialogViewModel viewModel)
        {
            viewModel.RunCommand.Execute(null);
        }
    }
}
