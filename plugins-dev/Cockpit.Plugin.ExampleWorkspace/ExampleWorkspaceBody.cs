using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.ExampleWorkspace;

/// <summary>
/// The whole body of an Example workspace, drawn by this plugin — the host draws only the tab and the frame. The
/// session in the middle is a real host session the plugin embedded through
/// <see cref="IWorkspaceContext.EmbedSession"/>, not a mock: the host owns its lifetime and keeps it out of the
/// session grid, and this control only places its view. That is the whole point of the example — a plugin owning a
/// full surface with a live session inside it.
/// </summary>
internal sealed class ExampleWorkspaceBody : UserControl
{
    public ExampleWorkspaceBody(IWorkspaceContext context)
    {
        // One embed, on the UI thread, as the body is built — the same shape a widget builds its view in. The host
        // starts the session and hands back the view to place; the plugin never manages the session's lifetime.
        var embedded = context.EmbedSession(new EmbeddedSessionRequest());

        var header = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Colors.Gray, 0.25),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "Example workspace", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock
                    {
                        Text = "This whole surface is drawn by a plugin — the session below is a real host session embedded in it.",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                header,
                new ContentControl { Content = embedded.View, Margin = new Thickness(12) },
            },
        };
    }
}
