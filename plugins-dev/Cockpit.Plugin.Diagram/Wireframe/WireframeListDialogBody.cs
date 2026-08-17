using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cockpit.Plugin.Diagram.Wireframe;

// AC-873's "Wireframes" ⋯-item — DiagramListDialogBody's counterpart, minus a catalog to read: saving a wireframe
// to a file, and listing what is saved, is WF-4's job and has not landed yet. Until then this dialog is honest
// about having nothing to list rather than a stub that pretends to be the real thing.
internal sealed class WireframeListDialogBody : UserControl
{
    public WireframeListDialogBody()
    {
        var header = new TextBlock { Text = "Wireframes", FontWeight = FontWeight.Bold, FontSize = 14 };
        var empty = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(12),
            Children =
            {
                new TextBlock { Text = "Nog geen opgeslagen wireframes.", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock
                {
                    Text = "Opslaan komt in een volgende stap — start intussen een nieuw wireframe via \"Nieuw wireframe\".",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                },
            },
        };

        Content = new DockPanel { Children = { header, empty } };
        DockPanel.SetDock(header, Dock.Top);
        header.Margin = new Thickness(12, 12, 12, 0);
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
