using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

public partial class CockpitView
{
    private WorkspaceTabViewModel? _visibleActiveTab;
    private DispatcherTimer? _tabScrollTimer;
    private PointerEventArgs? _tabDragPointer;
    private int _tabScrollDirection;
    private double _tabViewportWidth;
    private (bool Left, bool Right, double Width)? _tabMaskState;
    private bool _tabDragStarted;

    private void OnWorkspaceTabsLayoutUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit || WorkspaceTabsScroll.Viewport.Width <= 0)
        {
            return;
        }

        var overflow = WorkspaceTabsScroll.Extent.Width > WorkspaceTabsHost.Bounds.Width + 0.5;
        cockpit.Workspaces.HasHiddenTabs = overflow;
        WorkspaceTabsLeft.IsVisible = true;
        WorkspaceTabsRight.IsVisible = true;
        var left = WorkspaceTabsScroll.Offset.X > 0.5;
        var right = WorkspaceTabsScroll.Offset.X + WorkspaceTabsScroll.Viewport.Width < WorkspaceTabsScroll.Extent.Width - 0.5;
        WorkspaceTabsLeft.IsEnabled = left;
        WorkspaceTabsRight.IsEnabled = right;
        var width = WorkspaceTabsScroll.Viewport.Width;
        var fade = Math.Min(0.5, 24 / width);
        if (_tabMaskState != (left, right, width))
        {
            _tabMaskState = (left, right, width);
            WorkspaceTabsScroll.OpacityMask = left || right ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new(left ? Colors.Transparent : Colors.Black, 0),
                    new(Colors.Black, fade),
                    new(Colors.Black, 1 - fade),
                    new(right ? Colors.Transparent : Colors.Black, 1),
                },
            } : null;
        }

        var active = cockpit.Workspaces.Tabs.FirstOrDefault(tab => tab.IsActive);
        var resized = Math.Abs(width - _tabViewportWidth) > 0.5;
        _tabViewportWidth = width;
        if (_draggingTab is not null || (ReferenceEquals(active, _visibleActiveTab) && !resized))
        {
            return;
        }

        if (active is not null && WorkspaceTabStrip.ContainerFromIndex(cockpit.Workspaces.Tabs.IndexOf(active)) is { } container
            && container.Bounds.Width > 0)
        {
            var offset = WorkspaceTabsScroll.Offset.X;
            if (container.Bounds.Right > offset + width - 24)
            {
                offset = container.Bounds.Right - width + 24;
            }
            if (container.Bounds.Left < offset + 24)
            {
                offset = container.Bounds.Left - 24;
            }
            SetWorkspaceTabOffset(offset);
            _visibleActiveTab = active;
        }
    }

    private void SetWorkspaceTabOffset(double offset)
    {
        WorkspaceTabsScroll.Offset = new Vector(Math.Clamp(offset, 0,
            Math.Max(0, WorkspaceTabsScroll.Extent.Width - WorkspaceTabsScroll.Viewport.Width)), 0);
    }

    private void ScrollWorkspaceTab(int direction)
    {
        var offset = WorkspaceTabsScroll.Offset.X;
        var edges = Enumerable.Range(0, WorkspaceTabStrip.ItemCount)
            .Select(index => WorkspaceTabStrip.ContainerFromIndex(index))
            .OfType<Control>().Select(tab => tab.Bounds.Left).ToList();
        var next = direction > 0
            ? edges.FirstOrDefault(edge => edge > offset + 0.5, WorkspaceTabsScroll.Extent.Width)
            : edges.LastOrDefault(edge => edge < offset - 0.5, 0);
        SetWorkspaceTabOffset(next);
    }

    private void OnWorkspaceTabsLeft(object? sender, RoutedEventArgs e) => ScrollWorkspaceTab(-1);
    private void OnWorkspaceTabsRight(object? sender, RoutedEventArgs e) => ScrollWorkspaceTab(1);

    private void OnWorkspaceTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        SetWorkspaceTabOffset(WorkspaceTabsScroll.Offset.X - (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) * 60);
        e.Handled = true;
    }

    private void UpdateWorkspaceTabDragScroll(PointerEventArgs e)
    {
        _tabDragPointer = e;
        var x = e.GetPosition(WorkspaceTabsHost).X;
        _tabScrollDirection = x < 32 ? -1 : x > WorkspaceTabsHost.Bounds.Width - 32 ? 1 : 0;
        if (_tabScrollDirection == 0)
        {
            _tabScrollTimer?.Stop();
            return;
        }
        if (_tabScrollTimer is null)
        {
            _tabScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _tabScrollTimer.Tick += (_, _) =>
            {
                SetWorkspaceTabOffset(WorkspaceTabsScroll.Offset.X + _tabScrollDirection * 24);
                if (_tabDragPointer is { } pointer)
                {
                    OnWorkspaceTabPointerMoved(WorkspaceTabsHost, pointer);
                }
            };
        }
        _tabScrollTimer.Start();
    }

    private void EndWorkspaceTabDrag(IPointer pointer)
    {
        OnWorkspaceTabCaptureLost(null, null);
        pointer.Capture(null);
    }

    private void OnWorkspaceTabCaptureLost(object? sender, PointerCaptureLostEventArgs? e)
    {
        _draggingTab = null;
        _tabDragStarted = false;
        _tabDragPointer = null;
        _tabScrollTimer?.Stop();
    }
}
