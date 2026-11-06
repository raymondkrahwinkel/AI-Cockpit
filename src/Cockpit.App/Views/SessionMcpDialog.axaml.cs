using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class SessionMcpDialog : Window
{
    public SessionMcpDialog()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionMcpDialogViewModel)
            {
                CockpitWindowChrome.Apply(this, "MCP servers", "Manage this session's MCP servers.");
            }
        };
        Opened += (_, _) =>
        {
            if (DataContext is SessionMcpDialogViewModel { CanAdd: true })
            {
                ServerPicker.Focus();
            }
            else
            {
                (_FirstRemoveButton() ?? _CloseButton())?.Focus();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            var controls = DialogBody.GetVisualDescendants().OfType<Control>()
                .Where(control => control is Button or ComboBox && control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
                .ToList();
            var index = controls.FindIndex(control => control.IsFocused || control.IsKeyboardFocusWithin);
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
            if (controls.Count > 0)
            {
                controls[(index + step + controls.Count) % controls.Count].Focus();
                e.Handled = true;
            }
        }
        if (e.Key == Key.Enter && ServerPicker.IsFocused && !ServerPicker.IsDropDownOpen
            && DataContext is SessionMcpDialogViewModel viewModel)
        {
            viewModel.AddCommand.Execute(null);
            e.Handled = true;
        }
        if (e.Key == Key.Escape && !ServerPicker.IsDropDownOpen)
        {
            Close();
            e.Handled = true;
        }
    }

    private Button? _FirstRemoveButton() => this.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(button => Equals(button.Content, "Remove") && button.IsEffectivelyEnabled);

    private Button? _CloseButton() => this.GetVisualDescendants().OfType<Button>()
        .FirstOrDefault(button => Equals(button.Content, "Close"));
}
