using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugins.Abstractions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

internal sealed class DockPanelHeaderActionsHost : StackPanel
{
    public DockPanelHeaderActionsHost()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Spacing = 4;
    }

    public void SetActions(IReadOnlyList<ToolbarAction> actions, Action<ToolbarAction, Exception> reportFailure)
    {
        Children.Clear();
        foreach (var action in actions)
        {
            Children.Add(_Button(action, reportFailure));
        }
    }

    private static Button _Button(ToolbarAction action, Action<ToolbarAction, Exception> reportFailure)
    {
        var button = new Button
        {
            Classes = { "Subtle" },
            Padding = new Thickness(6),
            Content = new MaterialIcon
            {
                Kind = action.Icon ?? MaterialIconKind.PuzzleOutline,
                Width = 15,
                Height = 15,
            },
        };
        ToolTip.SetTip(button, action.Title);
        AutomationProperties.SetName(button, action.Title);
        button.Click += async (_, _) =>
        {
            try
            {
                await action.OnInvoke();
            }
            catch (Exception exception)
            {
                reportFailure(action, exception);
            }
        };
        return button;
    }
}
