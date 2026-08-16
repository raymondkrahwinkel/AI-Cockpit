using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram;

// Read-only-turned-actionable log of what happened on one diagram or whiteboard surface (AC-848), now sourced from
// the surface registry's own journal rather than reconstructed from agent tool calls — that journal is what makes
// "Terugdraaien" real (AC-853): it holds both origins (operator and agent), not agent activity alone.
internal sealed class ActivityStrip : Border
{
    private readonly ICockpitHost _host;
    private readonly string _surfaceId;
    private readonly bool _whiteboard;
    private readonly IDiagramAccessRegistry? _diagramRegistry;
    private readonly IWhiteboardAccessRegistry? _whiteboardRegistry;
    private readonly Action<string>? _onJumpToObject;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _emptyLabel;
    private string? _paneId;
    private string? _origin;

    public ActivityStrip(ICockpitHost host, string surfaceId, bool whiteboard, Action<string>? onJumpToObject)
    {
        _host = host;
        _surfaceId = surfaceId;
        _whiteboard = whiteboard;
        _onJumpToObject = onJumpToObject;
        _diagramRegistry = whiteboard ? null : host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _whiteboardRegistry = whiteboard ? host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry : null;

        Height = 130;
        Background = _Brush("CockpitSecondaryBgBrush");
        BorderBrush = _Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);

        // Never blank: since AC-852/AC-854 dropped the diff-gate for these tools, this strip is the only place
        // an edit becomes visible at all, so nothing-yet has to say so rather than look empty.
        _emptyLabel = new TextBlock
        {
            Text = "Nog geen activiteit op dit oppervlak.",
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

        if (_diagramRegistry is not null)
        {
            _diagramRegistry.HistoryChanged += _OnHistoryChanged;
        }

        if (_whiteboardRegistry is not null)
        {
            _whiteboardRegistry.HistoryChanged += _OnHistoryChanged;
        }

        DetachedFromVisualTree += (_, _) =>
        {
            if (_diagramRegistry is not null)
            {
                _diagramRegistry.HistoryChanged -= _OnHistoryChanged;
            }

            if (_whiteboardRegistry is not null)
            {
                _whiteboardRegistry.HistoryChanged -= _OnHistoryChanged;
            }
        };

        _Refresh();
    }

    // The surface always knows which pane it is coupled to (DiagramWorkspaceBody/WhiteboardWorkspaceBody's own
    // _binding) — kept in step so an agent-authored row can show that session's name instead of a raw pane id.
    public void SetSession(string? paneId, string? name)
    {
        _paneId = paneId;
        _origin = name;
        _Refresh();
    }

    private void _OnHistoryChanged(string surfaceId)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Dispatcher.UIThread.Post(_Refresh);
    }

    private void _Refresh()
    {
        var rows = _whiteboard
            ? (_whiteboardRegistry?.History(_surfaceId) ?? []).Select(_WhiteboardRow).ToList()
            : (_diagramRegistry?.History(_surfaceId) ?? []).Select(_DiagramRow).ToList();

        _emptyLabel.IsVisible = rows.Count == 0;
        _rows.IsVisible = rows.Count > 0;
        _rows.Children.Clear();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            _rows.Children.Add(rows[i]);
        }
    }

    private Control _DiagramRow(DiagramHistoryEntry entry) =>
        _Row(
            entry.When,
            entry.Origin == "operator" ? "operator" : _origin ?? "agent",
            entry.Summary,
            entry.ObjectKey,
            entry.Reverted,
            canRevert: true,
            () => _diagramRegistry?.Revert(_surfaceId, entry.Id));

    private Control _WhiteboardRow(WhiteboardHistoryEntry entry) =>
        _Row(
            entry.When,
            entry.Origin == "operator" ? "operator" : _origin ?? "agent",
            entry.Summary,
            entry.ObjectId,
            entry.Reverted,
            canRevert: entry.Kind == WhiteboardHistoryKind.Place,
            () => _whiteboardRegistry?.Revert(_surfaceId, entry.Id));

    // One row, diagram or whiteboard alike, now that both sides feed the same shape of entry (AC-853): a "Terugdraaien"
    // that actually calls the registry, disabled once reverted or when this kind cannot be (yet) — see
    // WhiteboardHistoryKind.Erase's documented gap — with the reason surfaced as a toast rather than silently ignored.
    private Control _Row(DateTime when, string origin, string summary, string? objectKey, bool reverted, bool canRevert, Func<string?> revert)
    {
        var revertButton = new Button { Content = "Terugdraaien", Classes = { "Compact", "Subtle" }, FontSize = 10, IsEnabled = !reverted && canRevert };
        ToolTip.SetTip(
            revertButton,
            reverted ? "Deze bewerking is al teruggedraaid."
            : !canRevert ? "Het terughalen van een verwijderd object kan nog niet worden teruggedraaid."
            : "Draai deze bewerking terug.");
        revertButton.PointerPressed += (_, e) => e.Handled = true;
        revertButton.Click += (_, _) =>
        {
            if (revert() is { } reason)
            {
                _host.ShowToast(reason, PluginToastSeverity.Error);
            }
        };
        DockPanel.SetDock(revertButton, Dock.Right);

        var lines = new StackPanel
        {
            Opacity = reverted ? 0.55 : 1,
            Children =
            {
                new TextBlock { Text = $"{when:HH:mm:ss} · {origin}{(reverted ? " · teruggedraaid" : "")}", FontSize = 10, Opacity = 0.55 },
                new TextBlock
                {
                    Text = summary,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 480,
                    TextDecorations = reverted ? TextDecorations.Strikethrough : null,
                },
            },
        };

        var row = new DockPanel { Children = { revertButton, lines } };
        var border = new Border
        {
            Padding = new Thickness(6, 3),
            Cursor = objectKey is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default,
            Child = row,
        };

        if (objectKey is { } key)
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
}
