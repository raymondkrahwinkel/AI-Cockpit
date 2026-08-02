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

/// <summary>
/// AC-561: a right-click's whole action set (Rename, Duplicate, Clear context, Set status, Resume later, Clear
/// status, Move up, Move down, Close) routes through <see cref="CockpitView._InvokeSessionCommand"/> via each
/// <c>MenuItem</c>'s <c>Click</c> handler. Reported symptom: Rename opened no field on the clicked row and a
/// different session activated, for a user with several sessions open.
/// </summary>
/// <remarks>
/// <para>
/// Measured (see AC-561-progress.md for the full log): a real headless mouse right-click, resolved through
/// Avalonia's own hit-testing and <c>ContextRequested</c> pipeline (not a hand-picked sender), against a row picked
/// out of the visual tree — repeated for a fresh list, after a full pointer-driven drag-reorder
/// (<see cref="CockpitViewModel.MoveSessionToVisibleIndex"/>), and with a second Sessions workspace filtering
/// <see cref="CockpitViewModel.VisibleSessions"/> — always resolved the <em>correct</em> row via
/// <c>MenuItem.DataContext</c> (Popup DataContext inheritance, resolved at <c>ContextMenu.Open()</c>) in every one
/// of those three conditions. So candidate 1 (a stale/misrouted DataContext on the menu item itself) is ruled out.
/// </para>
/// <para>
/// The genuine, reproducible failure mode is candidate 2 and is exercised by
/// <see cref="AnAlreadyOpenMenu_SurvivesAReorderOfARowItDoesNotOwn"/>: an <em>already-open</em> context menu's
/// owning <c>Border</c> was torn down and rebuilt — silently closing the Popup — the moment any reorder shifted
/// <em>any</em> row's list position, even one the operator did not touch, because
/// <c>CockpitViewModel.VisibleSessions</c> used to hand back a fresh snapshot on every read. Avalonia's own
/// <c>ContextMenu.ControlDetachedFromVisualTree</c> closes the popup as soon as its owning control detaches, and a
/// click aimed at a menu item that has just vanished this way falls through to whatever the sidebar now shows at
/// that pixel — the "different session becomes active" half of the report. The fix makes
/// <c>VisibleSessions</c> diff into a stable <c>ObservableCollection</c> with in-place Add/Remove/Move instead, so
/// an unrelated row's container - and any Popup open on it - survives.
/// </para>
/// </remarks>
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

            var rows = _Rows(_Strip(view));
            Assert.Equal(2, rows.Count);
            var target = rows[1]; // second visible row = Session 3, the hidden Session 2 sits between them in Sessions
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
    // DataContext, so it could never fail independently of it - see AC-561-progress.md).
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

    /// <summary>
    /// AC-561's actual root cause, pinned down: before the fix, <c>VisibleSessions</c> returned a fresh
    /// <c>List&lt;&gt;</c> on every read and every change fired one <c>OnPropertyChanged(nameof(VisibleSessions))</c>
    /// - to Avalonia's binding system that reads as "everything is different", so the whole ItemsControl tore down
    /// and rebuilt every row container, including ones an unrelated reorder never touched. A row's ContextMenu is a
    /// Popup attached to its owning Border; Avalonia's own <c>ControlDetachedFromVisualTree</c> handler closes that
    /// Popup the moment the Border is torn down - so an already-open menu on session 2 was silently closed by
    /// moving session 3, and the next click landed on whatever the sidebar now showed underneath (the "different
    /// session becomes active" half of the report). The fix (<see cref="CockpitViewModel"/>'s
    /// <c>_SyncVisibleSessions</c>) diffs into the same <c>ObservableCollection</c> instance the ItemsControl is
    /// bound to instead, so a row not part of the diff keeps its container - and its open Popup - alive.
    /// Confirmed red against the pre-fix code and green against the fix (see AC-561-progress.md for the run).
    /// </summary>
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
}
