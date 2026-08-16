using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Diagram;

// Read-only log of what an agent did on one diagram or whiteboard surface (AC-848). Fed straight from
// ICockpitSessionObserver.ToolActivityObserved (AC-116) — no new registry plumbing — filtered to this surface's
// own object-edit tools and whichever pane is currently coupled to it. Never blank: with nothing to show yet it
// says so, because since AC-852/AC-854 removed the diff-gate for these tools, this strip is the only place an
// agent's edit becomes visible at all.
internal sealed class ActivityStrip : Border
{
    private const int MaxLines = 200;
    private static readonly string[] DiagramTools = ["add_node", "rename_node", "remove_node", "connect_nodes", "disconnect_nodes"];
    private static readonly string[] WhiteboardTools = ["place_on_whiteboard", "erase_whiteboard_object"];

    private readonly ICockpitHost _host;
    private readonly string _surfaceId;
    private readonly string _toolPrefix;
    private readonly HashSet<string> _tools;
    private readonly Action<string>? _onJumpToObject;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _emptyLabel;
    private readonly List<ActivityLine> _lines = [];
    private string? _paneId;
    private string? _origin;

    public ActivityStrip(ICockpitHost host, string surfaceId, bool whiteboard, Action<string>? onJumpToObject)
    {
        _host = host;
        _surfaceId = surfaceId;
        _toolPrefix = whiteboard ? "mcp__cockpit-whiteboard__" : "mcp__cockpit-diagram__";
        _tools = new HashSet<string>(whiteboard ? WhiteboardTools : DiagramTools, StringComparer.Ordinal);
        _onJumpToObject = onJumpToObject;

        Height = 130;
        Background = _Brush("CockpitSecondaryBgBrush");
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);

        _emptyLabel = new TextBlock
        {
            Text = "Deze sessie levert geen activiteit.",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(12, 6),
        };

        var header = new TextBlock { Text = "Activiteit", FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) };
        DockPanel.SetDock(header, Dock.Top);

        Child = new DockPanel
        {
            Children =
            {
                header,
                new ScrollViewer { Content = new Panel { Children = { _emptyLabel, _rows } } },
            },
        };

        host.Sessions.ToolActivityObserved += _OnToolActivity;
        DetachedFromVisualTree += (_, _) => host.Sessions.ToolActivityObserved -= _OnToolActivity;

        _Refresh();
    }

    // The surface always knows which pane it is coupled to (DiagramWorkspaceBody/WhiteboardWorkspaceBody's own
    // _binding) — this is that same fact, kept in step across recoupling so incoming activity is judged against
    // whoever is coupled right now, not whoever was coupled when the strip was built.
    public void SetSession(string? paneId, string? name)
    {
        _paneId = paneId;
        _origin = name;
    }

    private void _OnToolActivity(object? sender, SessionToolActivity activity)
    {
        if (activity.IsError || activity.PaneId != _paneId || !activity.ToolName.StartsWith(_toolPrefix, StringComparison.Ordinal))
        {
            return;
        }

        var tool = activity.ToolName[_toolPrefix.Length..];
        if (!_tools.Contains(tool) || _ParseSummary(tool, activity.ResultContent) is not { } summary)
        {
            return;
        }

        _lines.Add(new ActivityLine(DateTime.Now, _origin ?? "agent", summary, _ObjectKey(tool, activity.InputJson, activity.ResultContent)));
        if (_lines.Count > MaxLines)
        {
            _lines.RemoveAt(0);
        }

        _Refresh();
    }

    // Only a call that actually landed on this surface describes a change (AC-852/AC-854's `changed`/`placed`
    // fields) — a refusal or a call for a different surface says nothing this strip's line should claim happened.
    private string? _ParseSummary(string tool, string resultJson)
    {
        try
        {
            using var result = JsonDocument.Parse(resultJson);
            var root = result.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            if (!root.TryGetProperty("id", out var id) || id.GetString() != _surfaceId)
            {
                return null;
            }

            if (root.TryGetProperty("changed", out var changed))
            {
                return changed.GetString();
            }

            return tool switch
            {
                "place_on_whiteboard" => root.TryGetProperty("placed", out var placed) ? $"placed {placed.GetString()}" : "placed an object",
                "erase_whiteboard_object" => "erased an object",
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The object a click on this line should jump to — node/connection id for the diagram (same HoldKey convention
    // AC-852's hold uses), the placed object's id for the whiteboard. Null when nothing to jump to.
    private static string? _ObjectKey(string tool, string inputJson, string resultJson)
    {
        try
        {
            using var input = JsonDocument.Parse(inputJson);
            var args = input.RootElement;
            if (tool is "add_node" or "rename_node" or "remove_node")
            {
                return args.TryGetProperty("id", out var id) ? id.GetString() : null;
            }

            if (tool is "connect_nodes" or "disconnect_nodes")
            {
                return args.TryGetProperty("from", out var from) && args.TryGetProperty("to", out var to)
                    ? $"{from.GetString()}->{to.GetString()}"
                    : null;
            }

            if (tool == "erase_whiteboard_object")
            {
                return args.TryGetProperty("objectId", out var objectId) ? objectId.GetString() : null;
            }

            if (tool == "place_on_whiteboard")
            {
                using var result = JsonDocument.Parse(resultJson);
                return result.RootElement.TryGetProperty("objectId", out var placedId) ? placedId.GetString() : null;
            }
        }
        catch (JsonException)
        {
            // Falls through to null — a line with no jump target still shows the time/origin/summary.
        }

        return null;
    }

    private void _Refresh()
    {
        _emptyLabel.IsVisible = _lines.Count == 0;
        _rows.IsVisible = _lines.Count > 0;
        _rows.Children.Clear();
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            _rows.Children.Add(_Row(_lines[i]));
        }
    }

    private Control _Row(ActivityLine line)
    {
        var revert = new Button { Content = "Terugdraaien", Classes = { "Compact", "Subtle" }, FontSize = 10 };
        ToolTip.SetTip(revert, "Nog niet beschikbaar — terugdraaien komt in een volgende stap (AC-853).");
        revert.PointerPressed += (_, e) => e.Handled = true;
        revert.Click += (_, _) => _host.ShowToast(
            "Terugdraaien is nog niet beschikbaar — dat komt in een volgende stap (AC-853).",
            PluginToastSeverity.Information);
        DockPanel.SetDock(revert, Dock.Right);

        var lines = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = $"{line.When:HH:mm:ss} · {line.Origin}", FontSize = 10, Opacity = 0.55 },
                new TextBlock { Text = line.Summary, FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            },
        };

        var row = new DockPanel { Children = { revert, lines } };
        var border = new Border
        {
            Padding = new Thickness(6, 3),
            Cursor = line.ObjectKey is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Child = row,
        };

        if (line.ObjectKey is { } key)
        {
            border.PointerPressed += (_, e) =>
            {
                if (!e.Handled)
                {
                    _onJumpToObject?.Invoke(key);
                }
            };
        }

        return border;
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;

    private readonly record struct ActivityLine(DateTime When, string Origin, string Summary, string? ObjectKey);
}
