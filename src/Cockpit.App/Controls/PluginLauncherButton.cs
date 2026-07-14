using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Cockpit.App.Controls;

/// <summary>
/// A plugin's left-menu launcher (#14): the button that runs the plugin's action, with a gear at its right end
/// when the plugin has settings. The button the operator already reaches for is the shortest way to what
/// configures it — shorter than the walk through the plugin store to find the same gear there.
/// <para>
/// The gear is a button inside a button, so the press has to stop there: <see cref="Button.Click"/> is a bubbling
/// routed event, and left alone it would reach the launcher and open the plugin's dialog behind its own settings.
/// </para>
/// </summary>
internal sealed class PluginLauncherButton : Button
{
    // Avalonia's "Button" selector matches the type exactly, so without this a derived button is styled by nothing at
    // all: the cockpit's theme skips it, and the row loses the surface and border every other button in the sidebar
    // has. It is a button, and it should be styled as one.
    protected override Type StyleKeyOverride => typeof(Button);

    public PluginLauncherButton(string title, Action onInvoke, Action? onSettings = null)
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Click += (_, _) => onInvoke();

        var content = new DockPanel();

        if (onSettings is not null)
        {
            var gear = new Button
            {
                Content = CockpitIcons.Gear(),
                Classes = { "Subtle" },
                Padding = new Thickness(6, 2),
                Margin = new Thickness(6, 0, -6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(gear, $"{title} settings");
            gear.Click += (_, e) =>
            {
                e.Handled = true;
                onSettings();
            };
            DockPanel.SetDock(gear, Dock.Right);
            content.Children.Add(gear);
        }

        content.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
        });

        Content = content;
    }
}
