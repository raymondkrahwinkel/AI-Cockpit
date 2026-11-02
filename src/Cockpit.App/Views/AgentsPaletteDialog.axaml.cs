using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The Agents palette (AC-1933), opened the way the command palette is: the search box keeps focus, arrows and Tab move
// the selection, Enter opens, Esc closes without acting, and the host runs the chosen action once the dialog is gone.
public partial class AgentsPaletteDialog : Window
{
    public AgentsPaletteDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AgentsPaletteViewModel viewModel)
            {
                viewModel.CloseRequested += Close;
            }
        };
        Opened += (_, _) => QueryBox.Focus();
        // Tunnel on the window so these keys win over the TextBox's own handling.
        this.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AgentsPaletteViewModel viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                viewModel.Open(beside: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                break;
            case Key.Escape:
                viewModel.Close();
                break;
            case Key.Down:
                viewModel.Move(1);
                break;
            case Key.Up:
                viewModel.Move(-1);
                break;
            case Key.Tab:
                viewModel.MoveColumn(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is AgentsPaletteViewModel viewModel && (sender as Control)?.DataContext is AgentOverviewCardViewModel card)
        {
            viewModel.Select(card);
            viewModel.Open(beside: false);
        }
    }
}
