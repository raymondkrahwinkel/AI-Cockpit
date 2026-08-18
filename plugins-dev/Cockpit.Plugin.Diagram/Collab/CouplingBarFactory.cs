using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Diagram.Collab;

// The "agent connected" bar's chrome (AC-810/AC-834's precedent), shared by every collab surface (AC-870): title,
// robot pip, status label, chips, couple/disconnect actions. What differs per surface — coupling type, label
// wording, an extra action like the whiteboard's invite button — stays with the caller.
internal static class CouplingBarFactory
{
    public static CouplingBarParts Build(string documentTitle, IReadOnlyList<Control> extraActions)
    {
        var titleLabel = new TextBlock { Text = documentTitle, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var pip = new MaterialIcon { Kind = MaterialIconKind.RobotOutline, Width = 15, Height = 15 };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = SurfaceChrome.Brush("CockpitAccentBrush") };
        var readChip = SurfaceChrome.Chip();
        var editChip = SurfaceChrome.Chip();

        var disconnect = new Button { Content = "Disconnect", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        var couple = new Button { Content = "Couple…", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(couple);
        foreach (var extra in extraActions)
        {
            actions.Children.Add(extra);
        }

        actions.Children.Add(disconnect);

        var bar = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 4),
            Background = SurfaceChrome.Brush("CockpitSecondaryBgBrush"),
            BorderBrush = SurfaceChrome.Brush("CockpitAccentBrush"),
            BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    actions,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { titleLabel, pip, label, readChip, editChip },
                    },
                },
            },
        };
        DockPanel.SetDock(actions, Dock.Right);

        return new CouplingBarParts(bar, label, readChip, editChip, pip, couple, disconnect);
    }
}
