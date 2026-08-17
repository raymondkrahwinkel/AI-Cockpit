using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Diagram.Wireframe;

// The whole body of a wireframe window (AC-873), same shape as DiagramWorkspaceBody — read that one first.
// Deviations: measured against a fixed design canvas rather than a size read off a rendered picture, and no
// hand-editing yet — the source box stays read-only until WF-5.
internal sealed class WireframeWorkspaceBody : UserControl
{
    // AC-837 zoom/pan range and wheel feel, same constants as the diagram.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double WheelZoomStepBase = 1.15;
    private const double ButtonZoomStep = 1.25;

    // The design canvas a wireframe is measured against — wide enough that a desktop screen's whole layout needs
    // zoom/pan to see at once, which is the point of AC-837 here (a diagram's SVG carries its own natural size;
    // a wireframe's Grid star-sizing needs one handed to it instead).
    private static readonly Size CanvasSize = new(960, 640);

    private static readonly Cursor _PanCursor = new(StandardCursorType.Hand);
    private static readonly Cursor _PanningCursor = new(StandardCursorType.SizeAll);

    private readonly ICockpitHost _host;
    private readonly IWireframeAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly string _documentTitle;
    private readonly Panel _surface;
    private readonly Border _viewport;
    private readonly TextBlock _zoomLabel;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private readonly TextBlock _editChip;
    private readonly Button _coupleButton;
    private readonly Button _disconnectButton;
    private readonly ToggleButton _sourceToggle;
    private readonly TextBox _sourceBox;
    private readonly ActivityStrip _activityStrip;
    private readonly PresenceIndicators _presence;
    private double _zoom = 1.0;
    private Vector _panOffset;
    private bool _isFitMode = true;
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;
    private WireframeCoupling? _current;
    private SurfaceSessionBinding _sessionBinding;

    public WireframeWorkspaceBody(ICockpitHost host, WireframeDocument document, string? sessionPaneId)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IWireframeAccessRegistry)) as IWireframeAccessRegistry;
        _surfaceId = document.Id;
        _documentTitle = document.Title;

        // No fixed control size beyond the design canvas below: `_viewport` positions/scales `_surface` itself via
        // RenderTransform for zoom and pan, same as DiagramWorkspaceBody's `_surface`.
        _surface = new Panel
        {
            Width = CanvasSize.Width,
            Height = CanvasSize.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
        };
        _viewport = _BuildViewport();

        (_couplingBar, _couplingLabel, _readChip, _editChip, _coupleButton, _disconnectButton) = _BuildCouplingBar();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        (var toolbar, _zoomLabel) = _BuildToolbar();
        var journal = new WireframeActivityJournal(_registry);
        _activityStrip = new ActivityStrip(host, _surfaceId, journal, onJumpToObject: null);
        _presence = new PresenceIndicators(_surfaceId, journal, journal);

        Content = new DockPanel
        {
            Children = { toolbar, _couplingBar, _presence, _sourceToggle, _sourceBox, _activityStrip, _viewport },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);

        // AC-834: the session is named by whoever opened this window, never guessed. No pane id — or one whose
        // session is gone — lands on a not-live binding, which is the "no agent on this wireframe" state.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, _RefreshCouplingBar);
        _RenderInto(document.Text);
        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);

        if (_registry is not null)
        {
            // Subscribed before the surface is registered: a wireframe an agent asked for (open_wireframe) arrives
            // already coupled, and that change is announced from inside SurfaceOpened.
            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.TextChanged += _OnTextChanged;
            _registry.SurfaceOpened(_surfaceId, _documentTitle, document.Text);

            // A plain Couple — zero capabilities. read_wireframe/edit_wireframe still ask their own consent.
            if (_sessionBinding.IsLive)
            {
                _registry.Couple(_sessionBinding.PaneId, _surfaceId);
            }
        }

        // No registry (an older host) means coupling cannot be shown or offered at all (AC-834's precedent).
        _couplingBar.IsVisible = _registry is not null;
        _RefreshCouplingBar();

        DetachedFromVisualTree += (_, _) =>
        {
            _sessionBinding.Dispose();
            if (_registry is null)
            {
                return;
            }

            _registry.CouplingChanged -= _OnCouplingChanged;
            _registry.TextChanged -= _OnTextChanged;
            _registry.SurfaceClosed(_surfaceId);
        };
    }

    // The way out of "window open, no agent" — after the bound session ended or the operator disconnected.
    private void _Recouple(string paneId)
    {
        if (_sessionBinding.Recouple(paneId, p => _registry?.Couple(p, _surfaceId)) is { } reason)
        {
            _host.ShowToast(reason, PluginToastSeverity.Error);
            return;
        }

        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _RefreshCouplingBar();
    }

    private void _OnCouplingChanged(WireframeCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        _current = change.Coupling;
        Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshCouplingBar);
    }

    private void _OnTextChanged(string surfaceId, string text)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _RenderInto(text));
    }

    // AC-811's read-only bronvak always shows the raw source, parsed or not (AC-871: errors are data). A source
    // that does not parse draws the errors where the render would go, rather than freezing on the last good one.
    private void _RenderInto(string source)
    {
        _sourceBox.Text = source;
        var parsed = WireframeParser.Parse(source);
        Control content = parsed.Root is { } root ? WireframeRenderer.Render(root) : _BuildErrorPanel(parsed.Errors);

        _surface.Children.Clear();
        _surface.Children.Add(content);

        if (_isFitMode)
        {
            _ApplyFit();
        }
        else
        {
            _ApplyTransform();
        }
    }

    private static Control _BuildErrorPanel(IReadOnlyList<WireframeParseError> errors)
    {
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(16) };
        list.Children.Add(new TextBlock
        {
            Text = "Kan dit wireframe niet weergeven:",
            FontWeight = FontWeight.Bold,
            Foreground = WireframePalette.Ink,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var error in errors)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"Regel {error.Line}: {error.Message}",
                FontSize = WireframePalette.CaptionSize,
                Foreground = WireframePalette.Muted,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Background = WireframePalette.Paper,
            BorderBrush = WireframePalette.Outline,
            BorderThickness = new Thickness(1),
            Child = list,
        };
    }

    // The zoom/pan surface (AC-837): a plain Border, not a ScrollViewer — panning is our own RenderTransform math,
    // same shape as DiagramWorkspaceBody's viewport, minus the click/hand-edit handling WF-5 will add.
    private Border _BuildViewport()
    {
        var viewport = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = _surface };
        viewport.SizeChanged += (_, _) =>
        {
            if (_isFitMode)
            {
                _ApplyFit();
            }
        };
        viewport.AddHandler(InputElement.PointerWheelChangedEvent, _OnViewportWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewport.PointerPressed += _OnViewportPointerPressed;
        viewport.PointerMoved += _OnViewportPointerMoved;
        viewport.PointerReleased += (_, _) => _EndPan();
        viewport.PointerCaptureLost += (_, _) => _EndPan();
        return viewport;
    }

    private void _OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        _ZoomAround(e.GetPosition(_viewport), _zoom * Math.Pow(WheelZoomStepBase, e.Delta.Y));
    }

    private void _OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panPointerStart = e.GetPosition(_viewport);
        _panOffsetStart = _panOffset;
        e.Pointer.Capture(_viewport);
        _viewport.Cursor = _PanningCursor;
        e.Handled = true;
    }

    private void _OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
        {
            return;
        }

        _panOffset = _panOffsetStart + (e.GetPosition(_viewport) - _panPointerStart);
        _isFitMode = false;
        _ApplyTransform();
    }

    private void _EndPan()
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        _viewport.Cursor = _PanCursor;
    }

    private void _ZoomByButton(double factor) =>
        _ZoomAround(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2), _zoom * factor);

    private void _ZoomAround(Point anchor, double requestedZoom)
    {
        (_zoom, _panOffset) = DiagramZoomMath.ZoomAround(anchor, _panOffset, _zoom, requestedZoom, MinZoom, MaxZoom);
        _isFitMode = false;
        _ApplyTransform();
    }

    // "Passend maken": recomputed from the viewport's own SizeChanged (first layout, then every resize), so the
    // first render lands at true size and keeps filling the window across a move/resize (AC-873's survive-resize AC).
    private void _ApplyFit()
    {
        _isFitMode = true;
        var fitZoom = DiagramZoomMath.FitZoom(_viewport.Bounds.Size, CanvasSize, MinZoom, MaxZoom);
        if (fitZoom <= 0)
        {
            return;
        }

        _zoom = fitZoom;
        _panOffset = DiagramZoomMath.CenteredPanOffset(_viewport.Bounds.Size, CanvasSize, _zoom);
        _ApplyTransform();
    }

    private void _ApplyTransform()
    {
        _surface.RenderTransform = new MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _panOffset.X, _panOffset.Y));
        _zoomLabel.Text = $"{_zoom * 100:0}%";
    }

    // AC-811: the wireframe source is one click away — collapsed under the render, never only in memory. Always
    // read-only: hand-editing the DSL text directly is not this ticket's (or WF-5's) shape — edits go through the
    // registry's per-component path so the journal and "jij bewerkt" hold both see them.
    private static (ToggleButton Toggle, TextBox Box) _BuildSourceToggle()
    {
        var box = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            MaxHeight = 180,
            Margin = new Thickness(8, 0, 8, 8),
            IsVisible = false,
        };
        var toggle = new ToggleButton { Content = "Toon bron", Classes = { "Compact" }, Margin = new Thickness(8, 4) };
        toggle.IsCheckedChanged += (_, _) => box.IsVisible = toggle.IsChecked == true;
        return (toggle, box);
    }

    private (Border Toolbar, TextBlock ZoomLabel) _BuildToolbar()
    {
        // AC-837: zoom in/out + Fit, with the current level always on screen.
        var zoomOut = new Button { Content = "−", Classes = { "Compact" }, MinWidth = 28 };
        zoomOut.Click += (_, _) => _ZoomByButton(1 / ButtonZoomStep);
        var zoomLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Center, FontSize = 12 };
        var zoomIn = new Button { Content = "+", Classes = { "Compact" }, MinWidth = 28 };
        zoomIn.Click += (_, _) => _ZoomByButton(ButtonZoomStep);
        var fit = new Button { Content = "Fit", Classes = { "Compact" } };
        fit.Click += (_, _) => _ApplyFit();

        var zoomControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOut, zoomLabel, zoomIn, fit },
        };

        var bar = new DockPanel { Children = { zoomControls } };
        DockPanel.SetDock(zoomControls, Dock.Right);
        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel);
    }

    // The "agent connected" bar (AC-810/AC-834's precedent), always on screen: "no agent on this wireframe" is a
    // real state — after the session ended, or after Disconnect — not one the bar should hide from.
    private (Border Bar, TextBlock Label, TextBlock ReadChip, TextBlock EditChip, Button Couple, Button Disconnect) _BuildCouplingBar()
    {
        var parts = CouplingBarFactory.Build(_documentTitle, extraActions: []);
        parts.Pip.Foreground = SurfaceChrome.Brush("CockpitAccentBrush");
        parts.Disconnect.Click += (_, _) => _registry?.Disconnect(_surfaceId);
        parts.Couple.Click += (_, _) => _ShowSessionPicker(parts.Couple);

        return (parts.Bar, parts.Label, parts.ReadChip, parts.EditChip, parts.Couple, parts.Disconnect);
    }

    private void _ShowSessionPicker(Control anchor) => _sessionBinding.ShowSessionPicker(anchor, _Recouple);

    private void _RefreshCouplingBar()
    {
        var coupled = _current is not null;
        _disconnectButton.IsVisible = coupled;
        _coupleButton.IsVisible = !coupled;
        _readChip.IsVisible = coupled;
        _editChip.IsVisible = coupled;

        if (_current is not { } coupling)
        {
            _couplingLabel.Text = _sessionBinding.EndedSessionName is { } ended
                ? $"Sessie {ended} is afgelopen — dit venster blijft open."
                : "Geen agent gekoppeld.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _sessionBinding.DisplayName ?? coupling.SessionId;
        var readAt = coupling.LastReadAt is { } at ? $" · gelezen {at.ToLocalTime():HH:mm}" : "";
        _couplingLabel.Text = coupling.CanRead
            ? $"Agent connected — session {name}{readAt}"
            : $"Agent connected — session {name} (no capabilities granted yet)";
        _couplingLabel.Foreground = _Brush("CockpitAccentBrush");
        SurfaceChrome.SetChip(_readChip, "read_wireframe", coupling.CanRead);
        SurfaceChrome.SetChip(_editChip, "edit_wireframe", coupling.CanEdit);
    }

    private static IBrush? _Brush(string resourceKey) => SurfaceChrome.Brush(resourceKey);
}
