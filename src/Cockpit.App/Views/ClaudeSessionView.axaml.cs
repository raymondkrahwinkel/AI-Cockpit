using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class ClaudeSessionView : UserControl
{
    public ClaudeSessionView()
    {
        InitializeComponent();

        // Enter sends the message; Shift+Enter inserts a newline. Tunnel so we pre-empt the
        // TextBox's own Enter handling (which would otherwise insert a newline).
        InputBox.AddHandler(InputElement.KeyDownEvent, _OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    private void _OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        e.Handled = true;
        if (DataContext is ClaudeSessionViewModel { IsBusy: false } vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
        }
    }
}
