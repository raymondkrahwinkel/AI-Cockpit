using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons.Avalonia;
using Cockpit.Plugin.GitHubActions.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.Plugin.GitHubActions.UI;

// The Dashboard/dock-rail view of the branch's recent workflow runs (AC-1065), read-only — restart/cancel is its own
// gated ticket. Polls per instance like CiStatusHeaderControl (a run list has nothing to share across instances,
// unlike the pull-requests plugin's shared source), following the *active* session since a widget is not per session.
//
// AC-1394: follows the window's active session through ICockpitUiHost.ActiveSession* rather than
// IWidgetContext.Sessions — "the active session" is window work (D6, AC-1365), and asks the backend part for
// recent runs over the plugin's channel rather than shelling out to `gh`/`git` itself.
internal sealed class CiWorkflowRunsWidget : UserControl
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private readonly IWidgetContext _context;
    private readonly ICockpitUiHost _host;
    private readonly DispatcherTimer _refresh;
    private readonly TextBlock _status;
    private readonly StackPanel _rows;

    private int _loadToken;
    private CancellationTokenSource? _loadCts;

    public CiWorkflowRunsWidget(IWidgetContext context, ICockpitUiHost host)
    {
        _context = context;
        _host = host;

        _status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitTextFaintBrush"),
        };

        _rows = new StackPanel { Spacing = 1 };

        Content = new DockPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                new Border { [DockPanel.DockProperty] = Dock.Top, Padding = new Thickness(2, 0, 0, 4), Child = _status },
                new ScrollViewer { Content = _rows },
            },
        };

        context.RefreshRequested += (_, _) => _ = _LoadAsync();

        _refresh = new DispatcherTimer { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => _ = _LoadAsync();
    }

    private int _MaxItems() =>
        (_context.Storage.Get<CiWorkflowRunsWidgetConfig>(CiWorkflowRunsWidgetConfig.StorageKey)
            ?? CiWorkflowRunsWidgetConfig.Default).Sanitized().MaxItems;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _loadCts = new CancellationTokenSource();
        _host.ActiveSessionChanged += _OnActiveSessionChanged;
        _refresh.Start();
        _ = _LoadAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _host.ActiveSessionChanged -= _OnActiveSessionChanged;
        _refresh.Stop();
        // Cancel any in-flight channel call so a hung request cannot outlive the closed panel.
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void _OnActiveSessionChanged(object? sender, EventArgs e) => _ = _LoadAsync();

    private async Task _LoadAsync()
    {
        var directory = _host.ActiveSessionWorkingDirectory;
        if (string.IsNullOrEmpty(directory) || _loadCts is not { } cts)
        {
            _Render([]);
            _Say("No active session with a working directory.");
            return;
        }

        var token = ++_loadToken;
        IReadOnlyList<CiRun> runs;
        try
        {
            runs = await _RecentRunsAsync(directory, _MaxItems(), cts.Token);
        }
        catch (Exception)
        {
            runs = [];
        }

        if (token != _loadToken)
        {
            return; // a newer load superseded this one
        }

        _Render(runs);
        _Say(runs.Count == 0 ? "No workflow runs found for this branch." : null);
    }

    private async Task<IReadOnlyList<CiRun>> _RecentRunsAsync(string workingDirectory, int limit, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new GitHubActionsRunsRequest(workingDirectory, limit), GitHubActionsChannel.Json);
        var answer = await _host.Channel.InvokeAsync(GitHubActionsChannel.RecentRuns, payload, cancellationToken);
        return answer.Deserialize<IReadOnlyList<CiRun>>(GitHubActionsChannel.Json) ?? [];
    }

    private void _Render(IReadOnlyList<CiRun> runs)
    {
        _rows.Children.Clear();
        foreach (var run in runs)
        {
            _rows.Children.Add(_BuildRow(run));
        }
    }

    private Control _BuildRow(CiRun run)
    {
        var (kind, brush) = CiRunPresentation.Appearance(run.State);
        var icon = new MaterialIcon { Kind = kind, Foreground = brush, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        var workflow = new TextBlock
        {
            Text = string.IsNullOrEmpty(run.WorkflowName) ? "workflow" : run.WorkflowName,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var branch = new TextBlock
        {
            Text = run.Branch,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextFaintBrush"),
        };

        var when = run.CreatedAt is { } at ? CiRunPresentation.Ago(at) : string.Empty;
        var detail = new TextBlock
        {
            Text = $"{CiRunPresentation.Duration(run.Duration)} · {when}",
            FontSize = 10,
            Foreground = _Brush("CockpitTextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(detail, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(detail);
        line.Children.Add(new StackPanel { Spacing = 1, Children = { workflow, branch } });

        var row = new Button
        {
            Classes = { "Subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(7, 5),
            Content = line,
        };
        ToolTip.SetTip(row, $"{workflow.Text} on '{run.Branch}' ({run.Event})\n\nClick to open the run on GitHub.");
        row.Click += (_, _) => CiRunLinks.OpenRunInBrowser(run.Url);

        return row;
    }

    private void _Say(string? message)
    {
        _status.Text = message ?? string.Empty;
        _status.IsVisible = !string.IsNullOrEmpty(message);
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
