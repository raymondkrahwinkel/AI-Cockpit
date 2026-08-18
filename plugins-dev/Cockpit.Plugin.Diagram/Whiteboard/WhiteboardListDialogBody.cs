using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// W-2/AC-843: the saved-boards overview, DiagramListDialogBody's counterpart — "Open" reopens the picked board
// straight from its file (WhiteboardCatalog.Load), coupled to the session already active when the dialog opened.
internal sealed class WhiteboardListDialogBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly StackPanel _list;

    public WhiteboardListDialogBody(ICockpitHost host)
    {
        _host = host;
        _list = new StackPanel { Spacing = 6, Margin = new Thickness(12) };

        var refresh = new Button { Content = "Refresh", Classes = { "Compact" } };
        refresh.Click += (_, _) => _ = _LoadAsync();

        // AC-896: "New whiteboard" moved here from the "..." menu, next to Refresh.
        var newWhiteboard = new Button { Content = "New whiteboard", Classes = { "Compact" } };
        newWhiteboard.Click += (_, _) => _ = _QuickStartAsync();

        var header = new DockPanel
        {
            Margin = new Thickness(12, 12, 12, 0),
            Children = { refresh, newWhiteboard, new TextBlock { Text = "Whiteboards", FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(newWhiteboard, Dock.Right);

        var activePaneId = host.Sessions.ActivePaneId;
        var sessionLabel = host.Sessions.ActiveSessionUsage?.ProfileLabel ?? activePaneId;
        var couplingNote = new TextBlock
        {
            Text = activePaneId is null
                ? "No active session — open one to link a whiteboard to it."
                : $"Links to {sessionLabel} — the session alongside.",
            FontSize = 11,
            Margin = new Thickness(12, 6, 12, 0),
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        DockPanel.SetDock(couplingNote, Dock.Top);

        Content = new DockPanel { Children = { header, couplingNote, new ScrollViewer { Content = _list } } };
        DockPanel.SetDock(header, Dock.Top);

        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        var rows = await _host.GetProjectMemoryRowsAsync();
        _Render(WhiteboardCatalog.List(rows), rows.Count);
    }

    // W-2/AC-843/AC-896: DiagramListDialogBody._QuickStartAsync's counterpart — an unsaved board starts empty,
    // named for what the operator asked for, and only ever gets a file once it is first saved (AC-812's rule).
    private async Task _QuickStartAsync()
    {
        if (await WhiteboardQuickStartDialog.ShowAsync(_host, "New whiteboard") is not { } quickStart)
        {
            return;
        }

        await WhiteboardWindow.OpenAsync(_host, new WhiteboardDocument(title: quickStart.Name), quickStart.SessionPaneId);
    }

    private void _Render(IReadOnlyList<WhiteboardEntry> entries, int memoryRowCount)
    {
        _list.Children.Clear();

        if (memoryRowCount == 0)
        {
            _list.Children.Add(_EmptyState(
                "This project has no memory location configured yet.",
                "Add one in the project editor before you can save a whiteboard here."));
            return;
        }

        if (entries.Count == 0)
        {
            _list.Children.Add(_EmptyState(
                "No whiteboards yet.",
                "Start one with \"New whiteboard\" — it appears here once you save it."));
            return;
        }

        var showHome = entries.Select(entry => entry.HomeLabel).Distinct().Count() > 1;
        foreach (var entry in entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Children.Add(_Row(entry, showHome));
        }
    }

    private Control _Row(WhiteboardEntry entry, bool showHome)
    {
        var title = new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var info = new StackPanel { Spacing = 2, Children = { title } };
        if (showHome)
        {
            info.Children.Add(new TextBlock { Text = entry.HomeLabel, FontSize = 10, Foreground = _Brush("CockpitTextSecondaryBrush") });
        }

        var open = new Button { Content = "Open", Classes = { "Compact" } };
        open.Click += (_, _) =>
        {
            if (_host.Sessions.ActivePaneId is not { } paneId)
            {
                _host.ShowToast("No active session to link this whiteboard to.", PluginToastSeverity.Information);
                return;
            }

            WhiteboardDocument document;
            try
            {
                document = WhiteboardCatalog.Load(entry.FilePath);
            }
            catch (Exception exception)
            {
                _host.ShowToast($"Opening failed: {exception.Message}", PluginToastSeverity.Error);
                return;
            }

            // Keyed on the file path (WhiteboardWindow.KeyFor), so opening the same board again brings the
            // existing window forward instead of a second one.
            _ = WhiteboardWindow.OpenAsync(_host, document, paneId);
        };

        var deleteButton = new Button { Content = "Delete", Classes = { "Compact" } };
        deleteButton.Click += (_, _) =>
        {
            WhiteboardCatalog.Delete(entry.FilePath);
            _ = _LoadAsync();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { open, deleteButton } };
        var row = new DockPanel { Children = { actions, info } };
        DockPanel.SetDock(actions, Dock.Right);

        return new Border
        {
            Padding = new Thickness(10, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = row,
        };
    }

    private static Control _EmptyState(string headline, string detail) => new StackPanel
    {
        Spacing = 4,
        Margin = new Thickness(12),
        Children =
        {
            new TextBlock { Text = headline, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = detail, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = _Brush("CockpitTextSecondaryBrush") },
        },
    };

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
