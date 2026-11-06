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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SessionMcpDialogViewModel)
            {
                CockpitWindowChrome.Apply(this, "MCP servers", "See this session's MCP servers and remove one when needed.");
            }
        };
        Opened += (_, _) =>
        {
            (_FirstRemoveButton() ?? _CloseButton())?.Focus();
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            var remove = _FirstRemoveButton();
            var close = _CloseButton();
            (close?.IsFocused == true || e.KeyModifiers == KeyModifiers.Shift ? remove : close ?? remove)?.Focus();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
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
