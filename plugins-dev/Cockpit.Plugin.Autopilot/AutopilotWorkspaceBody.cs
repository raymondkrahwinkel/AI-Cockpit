using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The Autopilot workspace body: the plugin draws the whole surface (the host draws only the tab and the frame).
/// This build (AC-149) is the shell — a header and a "no run yet" empty state. The run pipeline, its live embedded
/// session (<see cref="IWorkspaceContext.EmbedSession"/>) and the done-gate land in later sub-tickets; the
/// <paramref name="context"/> and <paramref name="settings"/> are the seams they build on.
/// </summary>
internal sealed class AutopilotWorkspaceBody : UserControl
{
    // The context and settings are unused by the shell — a later sub-ticket embeds a session through the context and
    // reads gate config from the settings. The workspace type's factory hands both in, so the seam sits here rather
    // than in a constructor signature that changes when the run flow lands.
    public AutopilotWorkspaceBody(IWorkspaceContext context, AutopilotSettings settings)
    {
        var header = new Border
        {
            Padding = new Thickness(16, 12),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            [DockPanel.DockProperty] = Dock.Top,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = "Autopilot", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                    new TextBlock
                    {
                        Text = "Start a run from an issue's context menu — the pipeline, its live session and the done-gate will appear here.",
                        Opacity = 0.7,
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    },
                },
            },
        };

        var emptyState = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            MaxWidth = 380,
            Children =
            {
                new MaterialIcon
                {
                    Kind = MaterialIconKind.RobotOutline,
                    Width = 32,
                    Height = 32,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
                new TextBlock { Text = "No run yet", HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "Pick “Start in Autopilot” on an issue, and its run lands on this surface.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
            },
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children = { header, emptyState },
        };
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
