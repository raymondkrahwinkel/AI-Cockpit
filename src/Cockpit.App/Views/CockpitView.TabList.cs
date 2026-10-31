using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// One line of the all-tabs list. Built when the list opens, so it can say which tabs are scrolled out of view.
public sealed record WorkspaceTabListRow(WorkspaceTabViewModel Tab, bool IsHidden, bool CanClose)
{
    public FontWeight Weight => FontWeight.Normal;

    public FontStyle Style => Tab.IsPreview ? FontStyle.Italic : FontStyle.Normal;

    // Tabs already in view are dimmed; the active one never is.
    public double Opacity => IsHidden || Tab.IsActive ? 1 : 0.6;
}

public partial class CockpitView
{
    private void OnWorkspaceTabListOpening(object? sender, EventArgs e) => _FillTabList(_TabListContent(), "");

    private void OnWorkspaceTabListOpened(object? sender, EventArgs e)
    {
        if (_TabListContent() is { } content && content.Children.OfType<TextBox>().FirstOrDefault() is { } filter)
        {
            filter.Text = "";
            Dispatcher.UIThread.Post(() => filter.Focus());
        }
    }

    private void OnWorkspaceTabListFilterChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { Parent: StackPanel content } box)
        {
            _FillTabList(content, box.Text ?? "");
        }
    }

    private StackPanel? _TabListContent() => WorkspaceTabsList.Flyout is Flyout { Content: StackPanel content } ? content : null;

    private void _FillTabList(StackPanel? content, string filter)
    {
        if (content is null || DataContext is not CockpitViewModel cockpit
            || content.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content is not ItemsControl list)
        {
            return;
        }

        var offset = WorkspaceTabsScroll.Offset.X;
        var viewport = WorkspaceTabsScroll.Viewport.Width;
        list.ItemsSource = cockpit.Workspaces.Tabs
            .Select((tab, index) => (tab, container: WorkspaceTabStrip.ContainerFromIndex(index)))
            .Where(pair => pair.tab.Name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(pair => new WorkspaceTabListRow(
                pair.tab,
                pair.container is not { } c || c.Bounds.Left < offset - 0.5 || c.Bounds.Right > offset + viewport + 0.5,
                cockpit.Workspaces.CanClose(pair.tab.Id)))
            .ToList();
    }

    private void OnWorkspaceTabListRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceTabListRow row } && DataContext is CockpitViewModel cockpit)
        {
            (WorkspaceTabsList.Flyout as Flyout)?.Hide();
            cockpit.Workspaces.SelectWorkspaceCommand.Execute(row.Tab.Id);
            // Selecting an already-active tab raises no layout change, so reveal it directly as well.
            Dispatcher.UIThread.Post(() => RevealWorkspaceTab(row.Tab), DispatcherPriority.Loaded);
        }
    }

    // Arrows move between the filter and the rows' buttons; Enter on the filter takes the first row; Escape closes.
    private void OnWorkspaceTabListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not StackPanel content)
        {
            return;
        }

        var stops = content.GetVisualDescendants().OfType<Control>()
            .Where(c => c is TextBox || (c is Button { IsVisible: true } b && b.Classes.Contains("closeWorkspace") == false && b.DataContext is WorkspaceTabListRow))
            .ToList();
        var at = stops.FindIndex(c => c.IsFocused || c.IsKeyboardFocusWithin && c is not TextBox);
        switch (e.Key)
        {
            case Key.Down when stops.Count > 0:
                stops[Math.Min(at + 1, stops.Count - 1)].Focus();
                e.Handled = true;
                break;
            case Key.Up when at > 0:
                stops[at - 1].Focus();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is TextBox && stops.Count > 1:
                stops[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                break;
            case Key.Escape:
                (WorkspaceTabsList.Flyout as Flyout)?.Hide();
                e.Handled = true;
                break;
        }
    }
}
