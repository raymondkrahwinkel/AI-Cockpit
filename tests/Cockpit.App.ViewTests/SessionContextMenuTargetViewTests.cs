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

    // AC-3, AC-4: a fresh list, right-clicking a row other than the first (so a fix that only happens to work on
    // index 0 cannot pass by accident) selects that row and Rename opens on it, prefilled with its own title.
    [Fact]
    public void RightClickRename_OpensOnTheClickedRow_WithItsOwnTitlePrefilled()
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var rows = _Rows(_Strip(view));
            var target = rows[1];
            var expected = (SessionPanelViewModel)target.DataContext!;

            _RightClick(window, target);
            Assert.Same(expected, cockpit.SelectedSession);

            _Click(_MenuItem(target, "Rename"));

            Assert.True(expected.IsRenaming, "the clicked row must open its inline rename field");
            Assert.Equal(expected.Title, expected.EditTitle);
            foreach (var other in cockpit.Sessions.Where(s => !ReferenceEquals(s, expected)))
            {
                Assert.False(other.IsRenaming, $"{other.Title} must not have been renamed instead");
            }

            window.Close();
        });
    }

    // AC-5 (reorder): a completed drag-reorder rebuilds row containers (per the ItemsControl's own AC-115
    // comment) - a right-click against the settled result must still land on the row actually under the cursor.
    [Fact]
    public void AfterADragReorder_RightClickRename_StillTargetsTheRowUnderTheCursor()
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var strip = _Strip(view);
            Border RowFor(SessionPanelViewModel s) => strip.GetVisualDescendants().OfType<Border>()
                .First(b => ReferenceEquals(b.DataContext, s));

            var s1 = cockpit.Sessions[0];
            var s3 = cockpit.Sessions[2];

            var from = RowFor(s1).TranslatePoint(new Point(5, 5), window)!.Value;
            var to = RowFor(s3).TranslatePoint(new Point(5, 5), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            for (var i = 1; i <= 5; i++)
            {
                window.MouseMove(from + (to - from) * (i / 5.0), RawInputModifiers.LeftMouseButton);
                window.UpdateLayout();
            }
            window.MouseUp(to, MouseButton.Left);
            window.UpdateLayout();

            // s1 dragged past s3: the sidebar order actually changed, proving the reorder took.
            Assert.NotEqual(cockpit.Sessions[0], cockpit.VisibleSessions.First());

            var rows = _Rows(strip);
            var target = rows[0]; // whatever now sits visually on top
            var expected = (SessionPanelViewModel)target.DataContext!;

            _RightClick(window, target);
            Assert.Same(expected, cockpit.SelectedSession);

            _Click(_MenuItem(target, "Rename"));
            Assert.True(expected.IsRenaming);

            window.Close();
        });
    }

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

    // AC-2: not just Rename - every one of the nine actions must read the same, correctly-resolved target. All
    // nine already share one helper (_InvokeSessionCommand), which reads sender.DataContext - the row's own
    // MenuItem, not a separate CommandParameter (removed: it inherited through the exact same chain as
    // DataContext, so it could never fail independently of it).
    [Theory]
    [InlineData("Rename")]
    [InlineData("Duplicate")]
    [InlineData("Clear context…")]
    [InlineData("Set status…")]
    [InlineData("Resume later…")]
    [InlineData("Clear status")]
    [InlineData("Move up")]
    [InlineData("Move down")]
    [InlineData("Close")]
    public void EveryContextMenuItem_CarriesTheRowsOwnSessionAsItsDataContext(string header)
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var rows = _Rows(_Strip(view));
            var target = rows[1]; // Session 2 - a SessionViewModel, so SupportsClearContext is true here
            var expected = (SessionPanelViewModel)target.DataContext!;

            _RightClick(window, target);

            var item = _MenuItem(target, header);
            Assert.Same(expected, item.DataContext);

            window.Close();
        });
    }

    // AC-7 regression: a right-click still must not arm the drag-reorder (AC-277) - a subsequent pointer move at
    // the same position must not reorder anything.
    [Fact]
    public void RightClick_DoesNotArmADrag()
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var before = cockpit.VisibleSessions.ToList();
            var rows = _Rows(_Strip(view));
            var target = rows[1];
            var point = target.TranslatePoint(new Point(5, 5), window)!.Value;

            window.MouseDown(point, MouseButton.Right);
            window.MouseMove(point + new Vector(0, 60), RawInputModifiers.RightMouseButton);
            window.UpdateLayout();

            Assert.Equal(before, cockpit.VisibleSessions.ToList());

            window.MouseUp(point, MouseButton.Right);
            window.Close();
        });
    }

    // AC-561 root cause: a fresh VisibleSessions list on every read rebuilt every row, closing the Popup of any open menu.
    [Fact]
    public void AnAlreadyOpenMenu_SurvivesAReorderOfARowItDoesNotOwn()
    {
        HeadlessAvalonia.Run(() =>
        {
            var (window, view, cockpit) = _BuildShownWindow();
            var strip = _Strip(view);
            var s2 = cockpit.Sessions[1];
            var s3 = cockpit.Sessions[2];
            s2.Statusline = "before";
            s3.Statusline = "untouched";
            var row = strip.GetVisualDescendants().OfType<Border>().First(b => ReferenceEquals(b.DataContext, s2));

            row.RaiseEvent(new ContextRequestedEventArgs());
            Assert.True(row.ContextMenu!.IsOpen);

            // Session 3 moves to the front, which shifts Session 2 from sidebar position 1 to 2 - Session 2's own
            // row was never the one dragged.
            cockpit.MoveSessionToVisibleIndex(cockpit.Sessions[2], 0);
            window.UpdateLayout();

            Assert.True(row.ContextMenu!.IsOpen, "a reorder of a row this menu does not own must not close it");

            // The still-open menu must still act on session 2 - not whatever the reorder now shows underneath it.
            _Click(_MenuItem(row, "Clear status"));
            Assert.Equal(string.Empty, s2.Statusline);
            Assert.Equal("untouched", s3.Statusline);

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

    // AC-703 (edge case 1, DoD): with no other Sessions workspace to move to, the operator gets visible feedback -
    // a disabled item - instead of the old silent no-op that looked identical to the timing bug from the outside.
    [Fact]
    public void MoveToWorkspace_IsDisabled_WhenNoOtherSessionsWorkspaceExists()
    {
        HeadlessAvalonia.Run(() =>
        {
            var cockpit = new CockpitViewModel();
            var onlyDesk = Workspace.Create("Sessions", WorkspaceType.Sessions);
            cockpit.Workspaces.Settings = new WorkspaceSettings { Workspaces = [onlyDesk], ActiveWorkspaceId = onlyDesk.Id };
            foreach (var session in cockpit.Sessions)
            {
                session.WorkspaceId = onlyDesk.Id;
            }

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            window.UpdateLayout();

            var rows = _Rows(_Strip(view));
            var target = rows[0];

            _RightClick(window, target);
            var moveItem = _MenuItem(target, "Move to workspace");

            Assert.False(moveItem.IsEnabled);
            Assert.Empty(moveItem.Items.OfType<MenuItem>());

            window.Close();
        });
    }
}
