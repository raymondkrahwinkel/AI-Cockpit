using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using Material.Icons.Avalonia;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// The Autopilot workspace body: the plugin draws the whole surface (the host draws only the tab and the frame). This
/// build (AC-150) shows either the "no run yet" empty state or the point a "Start in Autopilot" trigger loaded — it
/// tracks <see cref="AutopilotRunController.Current"/> and re-renders on its change, so a run started while the
/// workspace is already open appears without a rebuild. The run pipeline, its live embedded session
/// (<see cref="IWorkspaceContext.EmbedSession"/>) and the done-gate land in later sub-tickets; the
/// <paramref name="context"/> and <paramref name="settings"/> are the seams they build on.
/// </summary>
internal sealed class AutopilotWorkspaceBody : UserControl
{
    private readonly AutopilotRunController _runs;
    private readonly ContentControl _bodyHost = new();

    // The context and settings are unused by this build — a later sub-ticket embeds a session through the context and
    // reads gate config from the settings. The workspace type's factory hands them in, so the seam sits here rather
    // than in a constructor signature that changes when the run flow lands.
    public AutopilotWorkspaceBody(IWorkspaceContext context, AutopilotSettings settings, AutopilotRunController runs)
    {
        _runs = runs;

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

        Content = new DockPanel { LastChildFill = true, Children = { header, _bodyHost } };
        _Render();
    }

    // Subscribe only while on screen, and re-read the current run on the way in: a run may have started while this
    // workspace was in the background, and the operator is being brought to it now.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _runs.CurrentChanged += _OnCurrentChanged;
        _Render();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _runs.CurrentChanged -= _OnCurrentChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void _OnCurrentChanged(object? sender, EventArgs e) => _Render();

    private void _Render() =>
        _bodyHost.Content = _runs.Current is { } run ? _BuildRunView(run) : _BuildEmptyState();

    private Control _BuildRunView(AutopilotRun run)
    {
        var trackerChip = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = run.Tracker,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = _Brush("CockpitAccentBrush"),
            },
        };

        return new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 8,
            MaxWidth = 460,
            Children =
            {
                trackerChip,
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(run.IssueId) ? "(unnamed issue)" : run.IssueId,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 15,
                },
                new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(run.Title) ? "(no title)" : run.Title,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                },
                new TextBlock
                {
                    Text = "Loaded. The pipeline, its live session and the done-gate land on this surface in later sub-tickets.",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 12,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = _Brush("CockpitTextFaintBrush"),
                },
            },
        };
    }

    private Control _BuildEmptyState() =>
        new StackPanel
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

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
