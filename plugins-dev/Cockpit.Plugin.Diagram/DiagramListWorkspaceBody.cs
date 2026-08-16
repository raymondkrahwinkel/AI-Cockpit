using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Diagram;

// AC-826: the diagrams-per-project overview. Reads AC-812's <memory>/Diagrams/<slug>.md convention across every
// Memory row AC-827's read seam reports; "Open" hands the picked diagram to the next diagram.panel body via
// DiagramOpenHandoff.
internal sealed class DiagramListWorkspaceBody : UserControl
{
    private readonly ICockpitHost _host;
    private readonly StackPanel _list;

    public DiagramListWorkspaceBody(IWorkspaceContext context, ICockpitHost host)
    {
        _host = host;
        _list = new StackPanel { Spacing = 6, Margin = new Thickness(12) };

        var refresh = new Button { Content = "Refresh", Classes = { "Compact" } };
        refresh.Click += (_, _) => _ = _LoadAsync();

        var header = new DockPanel
        {
            Margin = new Thickness(12, 12, 12, 0),
            Children = { refresh, new TextBlock { Text = "Diagrams", FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center } },
        };
        DockPanel.SetDock(refresh, Dock.Right);

        Content = new DockPanel { Children = { header, new ScrollViewer { Content = _list } } };
        DockPanel.SetDock(header, Dock.Top);

        context.RefreshRequested += (_, _) => _ = _LoadAsync();
        _ = _LoadAsync();
    }

    private async Task _LoadAsync()
    {
        var rows = await _host.GetProjectMemoryRowsAsync();
        var entries = DiagramCatalog.List(rows);
        _Render(entries, rows.Count);
    }

    private void _Render(IReadOnlyList<DiagramEntry> entries, int memoryRowCount)
    {
        _list.Children.Clear();

        if (memoryRowCount == 0)
        {
            _list.Children.Add(_EmptyState(
                "This project has no memory location configured yet.",
                "Add one in the project editor before you can save a diagram here."));
            return;
        }

        if (entries.Count == 0)
        {
            _list.Children.Add(_EmptyState(
                "No diagrams yet.",
                "Start one with \"Diagram Builder\" — it appears here once you save it."));
            return;
        }

        var showHome = entries.Select(entry => entry.HomeLabel).Distinct().Count() > 1;
        foreach (var entry in entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _list.Children.Add(_Row(entry, showHome));
        }
    }

    private Control _Row(DiagramEntry entry, bool showHome)
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
            DiagramOpenHandoff.Pending = (entry.Title, entry.MermaidText);
            _ = _host.OpenWorkspaceAsync("diagram.panel");
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
            DiagramCatalog.Rename(entry.FilePath, newTitle);
            _ = _LoadAsync();
        };

        var deleteButton = new Button { Content = "Delete", Classes = { "Compact" } };
        deleteButton.Click += (_, _) =>
        {
            DiagramCatalog.Delete(entry.FilePath);
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
