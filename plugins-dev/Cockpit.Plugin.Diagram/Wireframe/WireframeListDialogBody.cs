using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram.Wireframe;

// AC-874/WF-4: the wireframes-per-project overview, DiagramListDialogBody's counterpart. "Open" opens the picked
// wireframe directly in its own window, coupled to the session already active when the dialog was opened.
internal sealed class WireframeListDialogBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly StackPanel _list;

    public WireframeListDialogBody(ICockpitHost host)
    {
        _host = host;
        _list = new StackPanel { Spacing = 6, Margin = new Thickness(12) };

        var refresh = new Button { Content = "Refresh", Classes = { "Compact" } };
        refresh.Click += (_, _) => _ = _LoadAsync();

        // AC-896: "New wireframe" moved here from the "..." menu, next to Refresh.
        var newWireframe = new Button { Content = "New wireframe", Classes = { "Compact" } };
        newWireframe.Click += (_, _) => _ = _QuickStartAsync();

        var header = new DockPanel
        {
            Margin = new Thickness(12, 12, 12, 0),
            Children = { refresh, newWireframe, new TextBlock { Text = "Wireframes", FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(newWireframe, Dock.Right);

        var activePaneId = host.Sessions.ActivePaneId;
        var sessionLabel = host.Sessions.ActiveSessionUsage?.ProfileLabel ?? activePaneId;
        var couplingNote = new TextBlock
        {
            Text = activePaneId is null
                ? "No active session — open one to link a wireframe to it."
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
        var entries = WireframeCatalog.List(rows);
        _Render(entries, rows.Count);
    }

    // AC-873/AC-896: DiagramListDialogBody._QuickStartAsync's counterpart — a quick-started wireframe opens with
    // the single childless "screen" line (WireframeDocument.New), never a bare document with nothing to render.
    private async Task _QuickStartAsync()
    {
        if (await SurfaceQuickStartDialog.ShowAsync(_host, "New wireframe", "wireframe.quickstart", "New wireframe", linkSessionByDefault: false, WireframeTemplates.All, WireframeTemplates.Preview) is not { } quickStart)
        {
            return;
        }

        await WireframeWindow.OpenAsync(_host, WireframeDocument.New(quickStart.Name, quickStart.TemplateSource), quickStart.SessionPaneId);
    }

    private void _Render(IReadOnlyList<WireframeEntry> entries, int memoryRowCount)
    {
        _list.Children.Clear();

        if (memoryRowCount == 0)
        {
            _list.Children.Add(_EmptyState(
                "This project has no memory location configured yet.",
                "Add one in the project editor before you can save a wireframe here."));
            return;
        }

        if (entries.Count == 0)
        {
            _list.Children.Add(_EmptyState(
                "No wireframes yet.",
                "Start one with \"New wireframe\" — it appears here once you save it."));
            return;
        }

        var showHome = entries.Select(entry => entry.HomeLabel).Distinct().Count() > 1;
        foreach (var entry in entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Children.Add(_Row(entry, showHome));
        }
    }

    private Control _Row(WireframeEntry entry, bool showHome)
    {
        var title = new TextBlock { Text = entry.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var renameBox = new TextBox { Text = entry.Title, IsVisible = false, MinWidth = 160 };

        var info = new StackPanel { Spacing = 2, Children = { title, renameBox } };
        if (showHome)
        {
            info.Children.Add(new TextBlock { Text = entry.HomeLabel, FontSize = 10, Foreground = _Brush("CockpitTextSecondaryBrush") });
        }

        var open = new Button { Content = "Open", Classes = { "Compact" } };
        open.Click += (_, _) =>
        {
            if (_host.Sessions.ActivePaneId is not { } paneId)
            {
                _host.ShowToast("No active session to link this wireframe to.", PluginToastSeverity.Information);
                return;
            }

            // Keyed on the file path (WireframeWindow.KeyFor), so opening the same wireframe again brings the
            // existing window forward instead of a second one.
            _ = WireframeWindow.OpenAsync(_host, new WireframeDocument(entry.FilePath, entry.Title, entry.WireframeText, entry.FilePath), paneId);
        };

        var renameButton = new Button { Content = "Rename", Classes = { "Compact" } };
        var saveButton = new Button { Content = "Save", Classes = { "Compact" }, IsVisible = false };
        var cancelButton = new Button { Content = "Cancel", Classes = { "Compact" }, IsVisible = false };

        renameButton.Click += (_, _) =>
        {
            renameBox.Text = entry.Title;
            title.IsVisible = false;
            renameBox.IsVisible = true;
            renameButton.IsVisible = false;
            saveButton.IsVisible = true;
            cancelButton.IsVisible = true;
        };
        cancelButton.Click += (_, _) =>
        {
            title.IsVisible = true;
            renameBox.IsVisible = false;
            renameButton.IsVisible = true;
            saveButton.IsVisible = false;
            cancelButton.IsVisible = false;
        };
        saveButton.Click += (_, _) =>
        {
            var newTitle = string.IsNullOrWhiteSpace(renameBox.Text) ? entry.Title : renameBox.Text!.Trim();
            WireframeCatalog.Rename(entry.FilePath, newTitle);
            _ = _LoadAsync();
        };

        var deleteButton = new Button { Content = "Delete", Classes = { "Compact" } };
        deleteButton.Click += (_, _) =>
        {
            WireframeCatalog.Delete(entry.FilePath);
            _ = _LoadAsync();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { open, renameButton, saveButton, cancelButton, deleteButton },
        };

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
