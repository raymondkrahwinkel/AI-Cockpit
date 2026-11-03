using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.ViewTests;

// AC-561: reported as Rename opening no field on the clicked row and a different session activating, with several open.
[Collection("avalonia")]
public class SessionContextMenuTargetViewTests
{
    private static (Window Window, CockpitView View, CockpitViewModel Cockpit) _BuildShownWindow(int width = 900, int height = 700)
    {
        var cockpit = new CockpitViewModel();
        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        window.UpdateLayout();
        return (window, view, cockpit);
    }

    private static ItemsControl _Strip(CockpitView view) =>
        view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "SessionListStrip");

    private static List<Border> _Rows(ItemsControl strip) => strip.GetVisualDescendants().OfType<Border>()
        .Where(b => b.DataContext is SessionPanelViewModel && b.ContextMenu is not null)
        .OrderBy(b => b.Bounds.Top)
        .ToList();

    private static void _RightClick(Window window, Border row)
    {
        var point = row.TranslatePoint(new Point(5, 5), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        window.UpdateLayout();
    }

    private static MenuItem _MenuItem(Border row, string header) =>
        row.ContextMenu!.Items.OfType<MenuItem>().First(m => (string)m.Header! == header);

    private static void _Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    // AC-5 (filter): with a second Sessions workspace hiding one of the sessions, the right-click must target the
    // visible row it hit - not miscount against the full, unfiltered session list.
    [Fact]
    public void WithAWorkspaceFilterActive_RightClickRename_TargetsTheVisibleRow()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = new CockpitViewModel();
            var desk1 = Workspace.Create("Desk1", WorkspaceType.Sessions);
            var desk2 = Workspace.Create("Desk2", WorkspaceType.Sessions);
            cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk1, desk2], ActiveWorkspaceId = desk1.Id };

            cockpit.Sessions[0].WorkspaceId = desk1.Id;
            cockpit.Sessions[1].WorkspaceId = desk2.Id; // hidden on the active desk
            cockpit.Sessions[2].WorkspaceId = desk1.Id;

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            window.UpdateLayout();

            // AC-1306: the strip is a workspace tree now, so Session 2 is on screen after all — under Desk2's own
            // node, below Desk1's two. Which row a right-click lands on is what this test is about, and the row it
            // has to land on is still the one at that pixel.
            var rows = _Rows(_Strip(view));
            Assert.Equal(3, rows.Count);
            var target = rows[1]; // second row under Desk1's node = Session 3; Desk2's node comes after both
            var expected = (SessionPanelViewModel)target.DataContext!;
            Assert.Equal("Session 3", expected.Title);

            _RightClick(window, target);
            Assert.Same(expected, cockpit.SelectedSession);

            _Click(_MenuItem(target, "Rename"));
            Assert.True(expected.IsRenaming);
            Assert.False(cockpit.Sessions[1].IsRenaming, "the hidden session must never be the one that gets renamed");

            window.Close();
        });
    }

    // AC-6: Close is the destructive one, tested on its own. A right-click Close on a row must close exactly that
    // session, not another, and must not touch anyone else's row. Deliberately the design-time "Session 1" (status
    // NeedsAttention): Busy/WorkingBackground sessions get an inline confirm prompt instead of closing on the first
    // click (a different, already-covered path), and this test is about which session Close targets, not that prompt.
    [Fact]
    public void ContextMenuClose_ClosesExactlyTheClickedSession()
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var expected = cockpit.Sessions[0];
            Assert.False(expected.RequiresCloseConfirmation, "the row this test closes must not need a confirm click first");
            var survivors = cockpit.Sessions.Where(s => !ReferenceEquals(s, expected)).ToList();

            var rows = _Rows(_Strip(view));
            var target = rows.First(r => ReferenceEquals(r.DataContext, expected));

            _RightClick(window, target);
            _Click(_MenuItem(target, "Close"));
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(expected, cockpit.Sessions);
            Assert.Equal(2, cockpit.Sessions.Count);
            foreach (var survivor in survivors)
            {
                Assert.Contains(survivor, cockpit.Sessions);
            }

            window.Close();
        });
    }

    // AC-703: "Move to workspace" did nothing - a second ContextMenu opened from a MenuItem's Click inside an
    // already-open one never showed (Avalonia raced the parent menu's close against the new popup's open). The fix
    // builds the submenu on the parent ContextMenu's Opened event instead, so it's there before anything can be clicked.
    [Fact]
    public void MoveToWorkspace_OpensASubmenu_AndMovesTheSessionWhenAnEntryIsClicked()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = new CockpitViewModel();
            var desk1 = Workspace.Create("Sessions", WorkspaceType.Sessions);
            var desk2 = Workspace.Create("Cockpit", WorkspaceType.Sessions);
            foreach (var session in cockpit.Sessions)
            {
                session.WorkspaceId = desk1.Id;
                desk1 = desk1.WithPane(new WorkspacePane(session.PaneId, PaneKind.AiSession));
            }

            cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [desk1, desk2], ActiveWorkspaceId = desk1.Id };

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            window.UpdateLayout();

            var rows = _Rows(_Strip(view));
            var target = rows[0];
            var expected = (SessionPanelViewModel)target.DataContext!;

            _RightClick(window, target);
            var moveItem = _MenuItem(target, "Move to workspace");

            Assert.True(moveItem.IsEnabled, "with a second Sessions workspace available, the item must be clickable");
            var subItem = Assert.Single(moveItem.Items.OfType<MenuItem>());
            Assert.Equal("Cockpit", subItem.Header);

            _Click(subItem);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(desk2.Id, expected.WorkspaceId);

            window.Close();
        });
    }
}
