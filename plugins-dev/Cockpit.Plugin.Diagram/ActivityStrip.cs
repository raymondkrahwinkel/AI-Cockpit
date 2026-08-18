using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram;

// Read-only-turned-actionable log of what happened on one collab surface (AC-848), sourced from the surface's own
// journal — that is what makes "Revert" real (AC-853). AC-870: the journal is picked by the caller (an
// ISurfaceActivityJournal) rather than a `bool whiteboard` this class branched on, so a third surface can supply its own.
internal sealed class ActivityStrip : Border
{
    private readonly ICockpitHost _host;
    private readonly string _surfaceId;
    private readonly ISurfaceActivityJournal _journal;
    private readonly Action<string>? _onJumpToObject;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly TextBlock _emptyLabel;
    private string? _paneId;
    private string? _origin;

    public ActivityStrip(ICockpitHost host, string surfaceId, ISurfaceActivityJournal journal, Action<string>? onJumpToObject)
    {
        _host = host;
        _surfaceId = surfaceId;
        _journal = journal;
        _onJumpToObject = onJumpToObject;

        Height = 130;
        Background = SurfaceChrome.Brush("CockpitSecondaryBgBrush");
        BorderBrush = SurfaceChrome.Brush("CockpitHairlineBrush");
        BorderThickness = new Thickness(0, 1, 0, 0);

        // Never blank: since AC-852/AC-854 dropped the diff-gate for these tools, this strip is the only place
        // an edit becomes visible at all, so nothing-yet has to say so rather than look empty.
        _emptyLabel = new TextBlock
        {
            Text = "No activity on this surface yet.",
            FontSize = 11,
            Opacity = 0.6,
            Margin = new Thickness(12, 6),
        };

        var header = new TextBlock { Text = "Activity", FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 6, 12, 2) };
        DockPanel.SetDock(header, Dock.Top);

        Child = new DockPanel
        {
            Children =
            {
                header,
                new ScrollViewer { Content = new Panel { Children = { _emptyLabel, _rows } } },
            },
        };

        _journal.HistoryChanged += _OnHistoryChanged;
        DetachedFromVisualTree += (_, _) => _journal.HistoryChanged -= _OnHistoryChanged;

        _Refresh();
    }

    // The surface always knows which pane it is coupled to (its own SurfaceSessionBinding) — kept in step so an
    // agent-authored row can show that session's name instead of a raw pane id.
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
        var rows = _journal.History(_surfaceId).Select(_EntryRow).ToList();

        _emptyLabel.IsVisible = rows.Count == 0;
        _rows.IsVisible = rows.Count > 0;
        _rows.Children.Clear();
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            _rows.Children.Add(rows[i]);
        }
    }

    private Control _EntryRow(SurfaceActivityEntry entry) =>
        _Row(
            entry.When,
            entry.Origin == "operator" ? "operator" : _origin ?? "agent",
            entry.Summary,
            entry.ObjectKey,
            entry.Reverted,
            entry.CanRevert,
            () => _journal.Revert(_surfaceId, entry.Id));

    // One row, diagram or whiteboard alike, now that both sides feed the same shape of entry (AC-853): a "Revert"
    // that actually calls the registry, disabled once reverted or when this kind cannot be (yet) — see
    // WhiteboardHistoryKind.Erase's documented gap — with the reason surfaced as a toast rather than silently ignored.
    private Control _Row(DateTime when, string origin, string summary, string? objectKey, bool reverted, bool canRevert, Func<string?> revert)
    {
        var revertButton = new Button { Content = "Revert", Classes = { "Compact", "Subtle" }, FontSize = 10, IsEnabled = !reverted && canRevert };
        ToolTip.SetTip(
            revertButton,
            reverted ? "This handling has already been reverted."
            : !canRevert ? "Restoring a deleted object cannot be reverted yet."
            : "Revert this handling.");
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
                new TextBlock { Text = $"{when:HH:mm:ss} · {origin}{(reverted ? " · reverted" : "")}", FontSize = 10, Opacity = 0.55 },
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
}
