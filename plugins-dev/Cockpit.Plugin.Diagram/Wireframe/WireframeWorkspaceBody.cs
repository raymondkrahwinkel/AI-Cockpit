using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Kind = Cockpit.Core.Wireframe.Model.WireframeNodeKind;

namespace Cockpit.Plugin.Diagram.Wireframe;

// The whole body of a wireframe window (AC-873, hand-editing AC-875), same shape as DiagramWorkspaceBody — read that
// one first. Deviation: measured against a fixed design canvas rather than a size read off a rendered picture, and a
// component is selected by clicking the control it was drawn as, which carries its own source node (AC-871).
internal sealed class WireframeWorkspaceBody : UserControl
{
    // AC-837 zoom/pan range and wheel feel, same constants as the diagram.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double WheelZoomStepBase = 1.15;
    private const double ButtonZoomStep = 1.25;

    // How far the pointer may travel before a press stops counting as a click on a component (AC-837's convention,
    // same slop as the diagram).
    private const double ClickSlopPx = 3;

    // AC-901: the title a screen added by hand starts out with — renamed with «Tekst…» or a double click, the same
    // way every other component's wording is changed.
    private const string NewScreenTitle = "New screen";

    // AC-914: same idea, for a state added from the toolbar.
    private const string NewStateTitle = "New state";

    private static readonly Cursor _PanCursor = new(StandardCursorType.Hand);
    private static readonly Cursor _PanningCursor = new(StandardCursorType.SizeAll);

    // AC-904: while a component is being dragged the pointer already says whether it may land where it is — a drop
    // the editor would refuse reads as "cannot" here, rather than as a toast after the fact.
    private static readonly Cursor _DropCursor = new(StandardCursorType.DragMove);
    private static readonly Cursor _NoDropCursor = new(StandardCursorType.No);

    private readonly ICockpitHost _host;
    private readonly IWireframeAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly string _documentTitle;
    private readonly Panel _surface;
    private readonly Panel _render;
    private readonly Canvas _overlay;
    private readonly Canvas _draft;
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
    private readonly AskStrip _askStrip;
    private readonly PresenceIndicators _presence;
    private readonly Button _saveButton;
    private readonly TextBlock _saveStatus;
    private readonly Button _addButton;
    private readonly Button _textButton;
    private readonly Button _deleteButton;
    private readonly Button _upButton;
    private readonly Button _downButton;
    private readonly Button _moveButton;
    private readonly Button _addScreenButton;
    private readonly StackPanel _stateStrip;
    private readonly Button _overviewButton;
    private readonly Button _viewportButton;
    private readonly Button _askButton;
    private readonly ToggleButton _notesToggle;
    private readonly TextBlock _handHint;
    private readonly StackPanel _propertiesContent;
    private readonly Border _notesPanel;
    private readonly StackPanel _notesContent;
    private double _zoom = 1.0;
    private Vector _panOffset;
    private bool _isFitMode = true;
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;
    private List<WireframeNode> _screens = [];
    // AC-915: the document's own sheet size, read off the source on every render — `_viewport` above is already
    // taken by the zoom/pan border, so this is named for what it holds instead.
    private WireframeViewport _canvasViewport = WireframeViewport.Desktop;
    private int _zoomedIndex = -1;
    private string? _zoomedId;
    // AC-914: which state of the zoomed screen is open, by id — null is the base screen itself. Self-heals every
    // render (see _RenderInto): a state removed from under it, or a switch to a different screen or the overview,
    // is exactly when it stops resolving, which is also when criterion 6 says it should be forgotten.
    private string? _stateId;
    private readonly Dictionary<string, ToggleButton> _stateChips = new(StringComparer.Ordinal);
    private string? _selectedId;
    private WireframeNode? _pressedOn;
    private Dictionary<WireframeNode, Control>? _controls;
    private WireframeDrag? _drag;
    private bool _placementHintShown;
    private WireframeCoupling? _current;
    private SurfaceSessionBinding _sessionBinding;
    private string? _filePath;
    private string _savedText;
    private string? _fileAsLastSeen;

    public WireframeWorkspaceBody(ICockpitHost host, WireframeDocument document, string? sessionPaneId)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IWireframeAccessRegistry)) as IWireframeAccessRegistry;
        _surfaceId = document.Id;
        _documentTitle = document.Title;
        _filePath = document.FilePath;
        _savedText = document.Text;
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);

        // No fixed control size beyond the design canvas: `_viewport` positions/scales `_surface` itself via
        // RenderTransform for zoom and pan, same as DiagramWorkspaceBody's `_surface`. AC-901 makes that canvas one
        // screen when zoomed in and the bounding box of every board in the overview, so each render sets it.

        // AC-875: the selection mark and the inline text box sit on their own canvas above the render, inside the same
        // transform — so zoom and pan move them with the wireframe rather than beside it. The render lives in its own
        // panel so re-rendering it leaves the overlay alone.
        _render = new Panel();
        _overlay = new Canvas();

        // AC-904: the gesture in flight gets a layer of its own — ghost and drop indicator, thrown away on release
        // (AC-898's draft layer), so nothing halfway between two places reaches the source, the journal or a reading
        // agent. Never hit-tested, so it cannot shadow the render the drop target is resolved against.
        _draft = new Canvas { IsHitTestVisible = false };
        _surface = new Panel
        {
            Width = _ScreenSize.Width,
            Height = _ScreenSize.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
            Children = { _render, _overlay, _draft },
        };
        _viewport = _BuildViewport();

        (_couplingBar, _couplingLabel, _readChip, _editChip, _coupleButton, _disconnectButton) = _BuildCouplingBar();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        (var toolbar, _zoomLabel, _saveButton, _saveStatus, _addButton, _textButton, _deleteButton,
            _upButton, _downButton, _moveButton, _addScreenButton, _stateStrip, _overviewButton, _viewportButton, _askButton, _notesToggle, _handHint) = _BuildToolbar();
        var journal = new WireframeActivityJournal(_registry);
        _activityStrip = new ActivityStrip(host, _surfaceId, journal, onJumpToObject: null);
        _askStrip = new AskStrip(_JumpToComponent);
        _presence = new PresenceIndicators(_surfaceId, journal, journal);
        var (propertiesPanel, propertiesContent) = _BuildPropertiesPanel();
        _propertiesContent = propertiesContent;
        // AC-907: its own column above the properties panel, not on the canvas — the fit-zoom and the overlay's own
        // sweep (AC-902) both work against a numbered list living there (see the grooming on this ticket).
        var (notesPanel, notesContent) = _BuildNotesPanel();
        _notesPanel = notesPanel;
        _notesContent = notesContent;
        var rightColumn = new DockPanel { Children = { _notesPanel, propertiesPanel } };
        DockPanel.SetDock(_notesPanel, Dock.Top);

        Content = new DockPanel
        {
            Children = { toolbar, _couplingBar, _presence, _sourceToggle, _sourceBox, _askStrip, _activityStrip, rightColumn, _viewport },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);
        DockPanel.SetDock(_askStrip, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);
        DockPanel.SetDock(rightColumn, Dock.Right);

        // AC-904 AC6: Escape gives up a drag where it stands, from wherever the focus happens to be inside this
        // window — tunnelled, so it is seen before the focused control gets its own say.
        AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);

        // AC-834: the session is named by whoever opened this window, never guessed. No pane id — or one whose
        // session is gone — lands on a not-live binding, which is the "no agent on this wireframe" state.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, _RefreshCouplingBar);
        _RenderInto(document.Text);
        _RefreshHandEditBar();
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

            if (_selectedId is { } stillHeld)
            {
                _registry.ReleaseComponent(_surfaceId, stillHeld);
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
        // A drag aims at controls this render is about to throw away, so the source changing under it — an agent's
        // edit, or the operator's own drop — ends the gesture rather than letting it point at a stale picture.
        _EndDrag();
        _controls = null;
        _sourceBox.Text = source;
        var parsed = WireframeParser.Parse(source);
        _screens = parsed.Screens.ToList();
        _canvasViewport = parsed.Viewport ?? WireframeViewport.Desktop;
        _ResolveZoomedScreen();

        // AC-914 criterion 6: a state that no longer resolves against the screen now showing — its own screen
        // changed, it was removed, or the surface returned to the overview — is exactly what "forgotten" means.
        if (_stateId is not null && _OpenState is null)
        {
            _stateId = null;
        }

        Control content = _screens.Count == 0
            ? _BuildErrorPanel(parsed.Errors)
            : _ZoomedScreen is { } screen
                ? _RenderZoomed(screen)
                : WireframeRenderer.Overview(_screens, _ScreenSize);

        var canvas = _CanvasSize;
        _surface.Width = canvas.Width;
        _surface.Height = canvas.Height;
        _render.Children.Clear();
        _render.Children.Add(content);
        _RefreshSelection();

        if (_isFitMode)
        {
            _ApplyFit();
        }
        else
        {
            _ApplyTransform();
        }

        _RefreshSaveBar();
    }

    // Every source change arrives as a fresh tree, so the selection is kept as the component's id and looked up again
    // (AC-906): it is either exactly the same component or it is gone, never the one that slid into its line.
    private void _RefreshSelection()
    {
        if (_selectedId is { } id && _Selected is null)
        {
            _registry?.ReleaseComponent(_surfaceId, id);
            _selectedId = null;
        }

        _presence.SetOperatorWriting(_selectedId is not null);
        _RefreshOverlay();
        _RefreshHandEditBar();
        _RefreshNotesPanel();
    }

    // The selected component in the tree as it stands right now, or null when nothing is selected or what was
    // selected has been removed.
    private WireframeNode? _Selected =>
        _selectedId is { } id ? WireframeHandEdit.Find(_screens, id) : null;

    // AC-907 criterion 8: the source and read_wireframe are unaffected by this — the switch is purely how much of
    // it this window is currently showing.
    private bool _NotesVisible => _notesToggle.IsChecked == true;

    // ---- The two views (AC-901): every screen side by side, or one of them filling the canvas ----

    // The screen the surface is zoomed into, or null while it shows the overview.
    private WireframeNode? _ZoomedScreen =>
        _zoomedIndex >= 0 && _zoomedIndex < _screens.Count ? _screens[_zoomedIndex] : null;

    private Size _CanvasSize =>
        _ZoomedScreen is null && _screens.Count > 0
            ? WireframeRenderer.OverviewSize(_screens.Count, _ScreenSize)
            : _ScreenSize;

    // AC-915: the sheet size the document's own viewport line names, desktop when it declares none.
    private Size _ScreenSize => WireframeRenderer.SizeOf(_canvasViewport);

    // AC-914: the state open in the zoomed screen, or null while showing its base — resolved by id against the
    // current tree on every call rather than kept as a node reference, the same reason _Selected works this way.
    private WireframeNode? _OpenState =>
        _stateId is { } id && _ZoomedScreen is { } screen ? WireframeHandEdit.Find(screen, id) : null;

    // The container an open state replaces, or null when none is open or its replaces: no longer resolves — the
    // second case falls back to the base screen rather than throwing mid-render.
    private WireframeNode? _OpenStateContainer =>
        _OpenState is { } state && _ZoomedScreen is { } screen && state.ValueOf(WireframeModifierName.Replaces) is { } value
            ? WireframeHandEdit.Find(screen, value.TrimStart('#'))
            : null;

    private Control _RenderZoomed(WireframeNode screen) =>
        _OpenState is { } state && _OpenStateContainer is { } container
            ? WireframeRenderer.RenderState(screen, container, state)
            : WireframeRenderer.Render(screen);

    // Which screen is zoomed into survives a re-render by its id where it has one, and by its place in the document
    // where it has none — a wireframe nobody has named yet carries no ids at all (AC-906). A document with one
    // screen is always shown zoomed, so a wireframe that has only ever had one behaves exactly as it did before.
    private void _ResolveZoomedScreen()
    {
        if (_screens.Count == 1)
        {
            _zoomedIndex = 0;
        }
        else if (_zoomedIndex >= 0)
        {
            var byId = _zoomedId is { } id ? _screens.FindIndex(screen => screen.Id == id) : -1;
            _zoomedIndex = byId >= 0 ? byId : Math.Min(_zoomedIndex, _screens.Count - 1);
        }

        _zoomedId = _ZoomedScreen?.Id;
    }

    private void _ZoomInto(WireframeNode screen)
    {
        _zoomedIndex = _screens.IndexOf(screen);
        _zoomedId = screen.Id;
        _isFitMode = true;
        _Redraw();
    }

    private void _ShowOverview()
    {
        _zoomedIndex = -1;
        _zoomedId = null;
        _isFitMode = true;
        _Redraw();
    }

    private void _Redraw() => _RenderInto(_sourceBox.Text ?? "");

    private static Control _BuildErrorPanel(IReadOnlyList<WireframeParseError> errors)
    {
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(16) };
        list.Children.Add(new TextBlock
        {
            Text = "Cannot render this wireframe:",
            FontWeight = FontWeight.Bold,
            Foreground = WireframePalette.Ink,
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var error in errors)
        {
            list.Children.Add(new TextBlock
            {
                Text = $"Line {error.Line}: {error.Message}",
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
    // same shape as DiagramWorkspaceBody's viewport.
    private Border _BuildViewport()
    {
        // Focusable since AC-904: a drag takes the focus so Escape reaches this window rather than whatever the
        // operator last clicked in the toolbar.
        var viewport = new Border { Background = Brushes.Transparent, ClipToBounds = true, Focusable = true, Child = _surface };
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
        viewport.PointerReleased += _OnViewportPointerReleased;
        viewport.PointerCaptureLost += (_, _) =>
        {
            _EndPan();
            _EndDrag();
        };
        viewport.DoubleTapped += (_, e) => _OnDoubleTapped(_NodeAt(e.GetPosition(_surface)));

        // AC-924: the component's own menu, built when it opens. A position is a real right-click and selects
        // whatever is under it; no position is the keyboard route — fall back to the current selection, and
        // open nothing if that is nothing either.
        viewport.ContextRequested += (_, args) =>
        {
            var hit = _Selected;
            if (args.TryGetPosition(_surface, out var point))
            {
                hit = _NodeAt(point);
            }

            if (hit is null)
            {
                return;
            }

            if (!ReferenceEquals(hit, _Selected))
            {
                _Select(hit);
            }

            viewport.ContextMenu = _BuildObjectContextMenu();
            viewport.ContextMenu.Open(viewport);
            args.Handled = true;
        };
        return viewport;
    }

    // AC-924: mirrors the toolbar exactly — same method, same IsEnabled. Add component… and Move to… open a popup
    // (AC-703): posted onto the dispatcher, anchored on the toolbar button, so neither ever opens from inside
    // this menu's own Click routing.
    private ContextMenu _BuildObjectContextMenu()
    {
        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                _MenuItemFor("Text…", _textButton, (_, _) => _StartTextEdit(_Selected)),
                _MenuItemFor("Add component…", _addButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _AddComponent(_addButton))),
                _MenuItemFor("Move up", _upButton, (_, _) => _Reorder(-1)),
                _MenuItemFor("Move down", _downButton, (_, _) => _Reorder(1)),
                _MenuItemFor("Move to…", _moveButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _MoveInto(_moveButton))),
                _MenuItemFor("Delete", _deleteButton, (_, _) => _DeleteSelected()),
                new Separator(),
                _MenuItemFor("Ask the agent…", _askButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _AddAsk(_askButton))),
            },
        };
    }

    // AC-924: one item, reading a toolbar button's own IsEnabled and tooltip — the menu never carries a second
    // enable-rule or a second wording of why something is off.
    private static MenuItem _MenuItemFor(string header, Button sameAs, EventHandler<RoutedEventArgs> onClick)
    {
        var item = new MenuItem { Header = header, IsEnabled = sameAs.IsEnabled };
        if (ToolTip.GetTip(sameAs) is { } tip)
        {
            ToolTip.SetTip(item, tip);
        }

        item.Click += onClick;
        return item;
    }

    // AC-901: in the overview a double click is how you step into a screen; inside one it stays what it was — the
    // way to change a component's wording.
    private void _OnDoubleTapped(WireframeNode? node)
    {
        if (_ZoomedScreen is not null)
        {
            _StartTextEdit(node);
            return;
        }

        if (node is not null && WireframeHandEdit.ScreenOf(_screens, node) is { } screen)
        {
            _ZoomInto(screen);
        }
    }

    private void _OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        _ZoomAround(e.GetPosition(_viewport), _zoom * Math.Pow(WheelZoomStepBase, e.Delta.Y), _NodeAt(e.GetPosition(_surface)));
    }

    // AC-837 still holds — a left-drag pans, a press that never travels selects — except in the one region AC-904
    // carves out: the component already selected drags instead. A deliberate departure from AC-841/AC-875, which kept
    // gestures apart; nothing is guessed here either, because the selection mark bounds the drag and is on screen.
    private void _OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // AC-924: focus lands here on every click, not only once a drag actually starts (_StartDrag's own Focus
        // below) — the keyboard route to the object menu (Menu key / Shift+F10) needs the viewport focused first.
        _viewport.Focus();

        var controls = _ControlMap();
        _pressedOn = _NodeAt(controls, e.GetPosition(_surface));
        _panPointerStart = e.GetPosition(_viewport);
        if (!_StartDrag(controls, e.GetPosition(_surface)))
        {
            _isPanning = true;
            _panOffsetStart = _panOffset;
            _viewport.Cursor = _PanningCursor;
        }

        e.Pointer.Capture(_viewport);
        e.Handled = true;
    }

    private void _OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        Vector travelled = e.GetPosition(_viewport) - _panPointerStart;
        if (_drag is { } drag)
        {
            _TrackDrag(drag, e.GetPosition(_surface), travelled.Length);
            return;
        }

        if (!_isPanning)
        {
            return;
        }

        _panOffset = _panOffsetStart + travelled;
        _isFitMode = false;
        _ApplyTransform();
    }

    private void _OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasPanning = _isPanning;
        Vector travel = e.GetPosition(_viewport) - _panPointerStart;
        var travelled = travel.Length;
        var dragged = _drag;
        _EndPan();
        _EndDrag();

        // A press on the selected component that never travels is still a click on it, so it stays selected and
        // nothing moves; below the slop the gesture was never a drag in the first place.
        if (dragged is not null && travelled > ClickSlopPx)
        {
            _Drop(dragged);
        }
        else if (wasPanning && travelled <= ClickSlopPx)
        {
            _Select(_pressedOn);
        }

        _pressedOn = null;
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

    // ---- Moving a component by dragging it (AC-904) ----

    // Only the selected component's own hit area drags: pressing one of the things inside it, or anything else, still
    // pans. Selecting it already minted the ids this gesture names components by (AC-906), so nothing has to be minted
    // mid-drag — which would rewrite the source under a gesture that has not decided anything yet.
    private bool _StartDrag(Dictionary<WireframeNode, Control> controls, Point onSurface)
    {
        if (_registry is null || _pressedOn is not { Id: { } id } node || id != _selectedId)
        {
            return false;
        }

        if (!controls.TryGetValue(node, out var control) || control.TranslatePoint(default, _surface) is not { } origin)
        {
            return false;
        }

        var indicator = new Border { BorderBrush = _Brush("CockpitAccentBrush"), CornerRadius = new CornerRadius(2), IsVisible = false };
        _drag = new WireframeDrag(id, controls, onSurface - origin, _Ghost(node, control.Bounds.Size), indicator);
        _draft.Children.Add(_drag.Ghost);
        _draft.Children.Add(indicator);
        _viewport.Focus();
        return true;
    }

    // The component itself, drawn again at the size it has on the surface — what is being moved, rather than a
    // stand-in for it.
    private static Control _Ghost(WireframeNode node, Size size) => new Panel
    {
        Width = size.Width,
        Height = size.Height,
        Opacity = 0.55,
        // Hidden until the press has travelled far enough to be a drag, so a plain click never flashes a second copy
        // of the component at the corner of the canvas.
        IsVisible = false,
        Children = { WireframeRenderer.Render(node) },
    };

    private void _TrackDrag(WireframeDrag drag, Point onSurface, double travelled)
    {
        Canvas.SetLeft(drag.Ghost, onSurface.X - drag.Grab.X);
        Canvas.SetTop(drag.Ghost, onSurface.Y - drag.Grab.Y);
        drag.Ghost.IsVisible = travelled > ClickSlopPx;

        drag.Target = null;
        drag.Indicator.IsVisible = false;
        if (travelled > ClickSlopPx)
        {
            _ResolveDrop(drag, onSurface);
        }

        _viewport.Cursor = travelled <= ClickSlopPx ? _PanCursor : drag.Target is null ? _NoDropCursor : _DropCursor;
    }

    // What the pointer is over, said in the two things a move names: the container, and the place inside it. A target
    // of null is every drop that will not happen — one the editor would refuse, or a free position nowhere near a
    // container — and `Refused` is what tells those two apart when the pointer is let go.
    private void _ResolveDrop(WireframeDrag drag, Point onSurface)
    {
        drag.Refused = false;
        if (_NodeAt(drag.Controls, onSurface) is not { } under)
        {
            return;
        }

        var container = under.IsContainer ? under : WireframeHandEdit.ParentOf(_screens, under);
        if (container?.Id is not { } parentId || !WireframeHandEdit.CanMoveInto(_screens, drag.Id, container)
            || _RectOf(drag.Controls, container) is not { } area)
        {
            drag.Refused = true;
            return;
        }

        // Over the container's own chrome rather than over anything inside it: in there, after what is already there.
        var children = container.Children.Select(child => _RectOf(drag.Controls, child)).OfType<Rect>().ToList();
        if (ReferenceEquals(container, under) || children.Count != container.Children.Count)
        {
            drag.Target = (parentId, null);
            _ShowIndicator(drag, area, outline: true);
            return;
        }

        var (index, line) = WireframeDropTarget.Resolve(children, area, onSurface);
        drag.Target = (parentId, index);
        _ShowIndicator(drag, line, outline: false);
    }

    // One control for both shapes the indicator takes: a filled line between two children, or an outline around the
    // container a drop would land at the end of.
    private static void _ShowIndicator(WireframeDrag drag, Rect area, bool outline)
    {
        drag.Indicator.Width = area.Width;
        drag.Indicator.Height = area.Height;
        drag.Indicator.BorderThickness = new Thickness(outline ? 2 : 0);
        drag.Indicator.Background = outline ? null : drag.Indicator.BorderBrush;
        drag.Indicator.IsVisible = true;
        Canvas.SetLeft(drag.Indicator, area.X);
        Canvas.SetTop(drag.Indicator, area.Y);
    }

    // Letting go: the whole gesture becomes one Move, so it is one line in the journal and one thing to take back.
    private void _Drop(WireframeDrag drag)
    {
        if (drag.Target is not { } target)
        {
            // A drop the editor would refuse already said so as a cursor, so it ends in silence. The one left is the
            // gesture this format genuinely has no words for.
            if (!drag.Refused)
            {
                _ShowFreePositionHint();
            }

            return;
        }

        // Landing back where it already was changes nothing, and the editor would only refuse it — so the gesture
        // simply ends, rather than warning about something the operator never asked for.
        if (WireframeHandEdit.Placement(_screens, drag.Id) is { Parent.Id: { } from } at && from == target.ParentId
            && (target.Position is { } position
                ? position == at.Index || position == at.Index + 1
                : at.Index == at.Parent.Children.Count - 1))
        {
            return;
        }

        _Apply(WireframeComponentEdit.Move(drag.Id, target.ParentId, target.Position));
    }

    // The format has no coordinates, so there is no free position to drop a component at and the next render would
    // put it straight back. Said once per window, and saying what did happen — which is nothing.
    private void _ShowFreePositionHint()
    {
        if (_placementHintShown)
        {
            return;
        }

        _placementHintShown = true;
        _host.ShowToast(
            "A wireframe places itself, so there is no free position to drop a component at and nothing was moved. Let go on a container, or between two components; free placement is what the whiteboard is for.",
            PluginToastSeverity.Information);
    }

    private void _EndDrag()
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        _draft.Children.Clear();
        _viewport.Cursor = _PanCursor;
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _drag is null)
        {
            return;
        }

        _EndDrag();
        e.Handled = true;
    }

    // One drag in flight: what is being moved, the controls it is aimed at, and where the pointer last said it would
    // land. Nothing here reaches the source until _Drop turns it into a single move.
    private sealed class WireframeDrag(
        string id,
        Dictionary<WireframeNode, Control> controls,
        Vector grab,
        Control ghost,
        Border indicator)
    {
        public string Id => id;

        public Dictionary<WireframeNode, Control> Controls => controls;

        // Where inside the component it was picked up, so the ghost does not jump to the pointer on the first move.
        public Vector Grab => grab;

        public Control Ghost => ghost;

        public Border Indicator => indicator;

        public (string ParentId, int? Position)? Target { get; set; }

        // Whether the pointer is over something that cannot take this component, as opposed to over nothing at all.
        public bool Refused { get; set; }
    }

    // ---- Hand-editing on the surface itself (AC-875) ----

    // Every drawn control by the component it came from. Walked once per render rather than per pointer event, and
    // dropped again by _RenderInto — a fresh render is a fresh tree of controls, and nothing here outlives it. The
    // places themselves are read from the controls at the moment they are asked for, so layout still moves freely.
    private Dictionary<WireframeNode, Control> _ControlMap()
    {
        if (_controls is { } cached)
        {
            return cached;
        }

        _controls = new Dictionary<WireframeNode, Control>();
        foreach (var control in _render.GetVisualDescendants().OfType<Control>())
        {
            if (WireframeSource.GetNode(control) is { } node)
            {
                _controls.TryAdd(node, control);
            }
        }

        return _controls;
    }

    // The component at a point: the smallest control carrying a source node (AC-871) whose place contains it. Read
    // off layout rather than hit-tested (AC-904) — a label is bare text with no background, so hit-testing fell
    // through it to the paper behind and selected the whole screen instead.
    private WireframeNode? _NodeAt(Point onSurface) => _NodeAt(_ControlMap(), onSurface);

    private WireframeNode? _NodeAt(Dictionary<WireframeNode, Control> controls, Point onSurface) => controls.Keys
        .Select(node => (Node: node, Rect: _RectOf(controls, node)))
        .Where(found => found.Rect is { } rect && rect.Contains(onSurface))
        .OrderBy(found => found.Rect!.Value.Width * found.Rect!.Value.Height)
        .Select(found => (WireframeNode?)found.Node)
        .FirstOrDefault();

    // A component's place on the surface, or null when it is not drawn — the overview leaves out what is too small to
    // show, and there is nothing to aim at then.
    private Rect? _RectOf(Dictionary<WireframeNode, Control> controls, WireframeNode node) =>
        controls.TryGetValue(node, out var control) && control.TranslatePoint(default, _surface) is { } origin
            ? new Rect(origin, control.Bounds.Size)
            : null;

    // Selecting is holding: while the operator has a component under their hand an agent's edit naming it is refused
    // with a reason (AC-872's hold), and every other component stays open to it. Taking one under their hand is also
    // what mints its id (AC-906) — until something names a component, the source stays free of ids.
    private void _Select(WireframeNode? node)
    {
        if (_selectedId is { } previous)
        {
            _registry?.ReleaseComponent(_surfaceId, previous);
        }

        _selectedId = node is null ? null : _registry?.EnsureComponentId(_surfaceId, node.Line);
        if (_selectedId is { } held)
        {
            _registry?.HoldComponent(_surfaceId, held);
        }

        _presence.SetOperatorWriting(_selectedId is not null);
        _RefreshOverlay();
        _RefreshHandEditBar();
        _RefreshNotesPanel();
    }

    // Posted rather than drawn straight away: the mark is placed from the selected control's own laid-out bounds, and
    // right after a render those are not measured yet.
    private void _RefreshOverlay() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(_DrawOverlay, Avalonia.Threading.DispatcherPriority.Loaded);

    private void _DrawOverlay()
    {
        // Only the marks are cleared; an inline text box in flight keeps its place, since a re-render underneath it
        // is exactly when the operator is still typing. AC-902: flow arrows are Path rather than Border (they are
        // not a rectangle), so they need their own sweep — left out, they stack up on every re-render.
        foreach (var mark in _overlay.Children.OfType<Border>().ToList())
        {
            _overlay.Children.Remove(mark);
        }

        foreach (var arrow in _overlay.Children.OfType<Avalonia.Controls.Shapes.Path>().ToList())
        {
            _overlay.Children.Remove(arrow);
        }

        // AC-902/AC-907: flows and notes are drawn whether or not anything is selected — neither is a selection mark.
        _DrawFlows();
        _DrawNoteMarkers();

        if (_Selected is not { } node || _ControlFor(node) is not { } control
            || control.TranslatePoint(default, _surface) is not { } origin)
        {
            return;
        }

        var bounds = new Rect(origin, control.Bounds.Size).Inflate(3);
        var outline = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            BorderThickness = new Thickness(2),
            BorderBrush = _Brush("CockpitAccentBrush"),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);
        _overlay.Children.Add(outline);
    }

    // AC-902 (WF-2): a `goto:` between screens, drawn as an arrow between boards in the overview or as a clickable
    // marker on the component itself when zoomed into one screen — both read the bounding boxes the render just
    // laid out, the same way the selection mark above does.
    private void _DrawFlows()
    {
        if (_ZoomedScreen is { } screen)
        {
            _DrawFlowMarkers(screen);
        }
        else
        {
            _DrawFlowArrows();
        }
    }

    private void _DrawFlowMarkers(WireframeNode screen)
    {
        var controls = _ControlMap();
        foreach (var node in _FlowSources(screen))
        {
            if (node.ValueOf(WireframeModifierName.Goto) is not { } title
                || WireframeGotoResolver.Resolve(_screens, title).Screen is not { } target
                || _RectOf(controls, node) is not { } bounds)
            {
                continue;
            }

            _Marker(bounds, _MarkerSide.Right, _Brush("CockpitAccentBrush"), null, null, $"Goes to «{target.Text}»", () => _ZoomInto(target));
        }
    }

    // AC-907: one component's requirements, drawn in both views (unlike the flow marker, which is a between-screens
    // arrow in the overview) — left-top, so a component carrying both a goto and a note shows two markers, one at
    // each corner, with no stacking logic between them.
    private void _DrawNoteMarkers()
    {
        if (!_NotesVisible)
        {
            return;
        }

        var controls = _ControlMap();
        foreach (var (_, notes) in _NoteGroups())
        {
            for (var index = 0; index < notes.Count; index++)
            {
                var node = notes[index];
                if (_RectOf(controls, node) is not { } bounds)
                {
                    continue;
                }

                _Marker(bounds, _MarkerSide.Left, _Brush("CockpitSecondaryBgBrush"), _Brush("CockpitHairlineBrush"),
                    _Numbered(index + 1), node.ValueOf(WireframeModifierName.Note) ?? "", () => _Select(node));
            }
        }
    }

    private enum _MarkerSide { Left, Right }

    // AC-902's marker, pulled out into the one shared helper AC-907's grooming asked for: a small round tag pinned
    // to a component's corner, clamped inside the canvas so a note on the screen line itself (origin (0,0)) does not
    // fall half off the edge.
    private void _Marker(Rect bounds, _MarkerSide side, IBrush? fill, IBrush? border, string? label, string tip, Action onTap)
    {
        var marker = new Border
        {
            Width = 16,
            Height = 16,
            Background = fill,
            BorderBrush = border,
            BorderThickness = border is null ? default : new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = new Cursor(StandardCursorType.Hand),
        };

        if (label is not null)
        {
            marker.Child = new TextBlock
            {
                Text = label,
                FontSize = 9,
                Foreground = _Brush("CockpitTextPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        ToolTip.SetTip(marker, tip);
        marker.Tapped += (_, _) => onTap();

        var canvas = _CanvasSize;
        var x = side == _MarkerSide.Right ? bounds.X + bounds.Width - 12 : bounds.X - 4;
        Canvas.SetLeft(marker, Math.Clamp(x, 0, Math.Max(0, canvas.Width - marker.Width)));
        Canvas.SetTop(marker, Math.Clamp(bounds.Y - 4, 0, Math.Max(0, canvas.Height - marker.Height)));
        _overlay.Children.Add(marker);
    }

    // Which screens' notes to draw/list — the whole document in the overview, just the one screen zoomed in — each
    // renumbered from ① so a note's marker always matches its position in the list beside it (criterion 7).
    private List<(WireframeNode Screen, List<WireframeNode> Notes)> _NoteGroups()
    {
        IEnumerable<WireframeNode> screens = _ZoomedScreen is { } zoomed ? [zoomed] : _screens;
        return screens
            .Select(screen => (Screen: screen, Notes: _NotesOf(screen).ToList()))
            .Where(group => group.Notes.Count > 0)
            .ToList();
    }

    private static IEnumerable<WireframeNode> _NotesOf(WireframeNode node)
    {
        if (node.Has(WireframeModifierName.Note))
        {
            yield return node;
        }

        foreach (var found in node.Children.SelectMany(_NotesOf))
        {
            yield return found;
        }
    }

    // ①…⑳, then a plain "(21)" — the format has no practical use for more notes on one screen than that.
    private static string _Numbered(int number) =>
        number is >= 1 and <= 20 ? char.ConvertFromUtf32(0x2460 + number - 1) : $"({number})";

    private void _DrawFlowArrows()
    {
        for (var index = 0; index < _screens.Count; index++)
        {
            foreach (var node in _FlowSources(_screens[index]))
            {
                if (node.ValueOf(WireframeModifierName.Goto) is not { } title
                    || WireframeGotoResolver.Resolve(_screens, title).Screen is not { } target || target == _screens[index]
                    || _ControlFor(node) is not { } control || control.TranslatePoint(default, _surface) is not { } origin)
                {
                    continue;
                }

                var source = new Rect(origin, control.Bounds.Size);
                var destination = WireframeRenderer.BoardBounds(_screens.IndexOf(target), _screens.Count, _ScreenSize);
                _overlay.Children.Add(_Arrow(_EdgePoint(source, destination.Center), _EdgePoint(destination, source.Center)));
            }
        }
    }

    private static IEnumerable<WireframeNode> _FlowSources(WireframeNode node)
    {
        if (node.Has(WireframeModifierName.Goto))
        {
            yield return node;
        }

        foreach (var found in node.Children.SelectMany(_FlowSources))
        {
            yield return found;
        }
    }

    // Where the line from `rect`'s center towards `toward` leaves `rect` — the arrow's end sits on the box, not
    // buried inside it.
    private static Point _EdgePoint(Rect rect, Point toward)
    {
        var center = rect.Center;
        var dx = toward.X - center.X;
        var dy = toward.Y - center.Y;
        if (dx == 0 && dy == 0)
        {
            return center;
        }

        var scale = Math.Min(
            dx == 0 ? double.PositiveInfinity : Math.Abs(rect.Width / 2 / dx),
            dy == 0 ? double.PositiveInfinity : Math.Abs(rect.Height / 2 / dy));
        return new Point(center.X + dx * scale, center.Y + dy * scale);
    }

    // A line with a filled triangular head, the same construction as the whiteboard's _PaintArrow — but as a
    // Shapes.Path rather than a DrawingContext painter, since this lives on the overlay canvas, not inside a Render
    // override.
    private static Avalonia.Controls.Shapes.Path _Arrow(Point from, Point to)
    {
        const double headSize = 10;
        const double wing = Math.PI / 7;
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        var back = to - new Vector(Math.Cos(angle), Math.Sin(angle)) * (headSize * 1.6);
        var left = to - new Vector(Math.Cos(angle - wing), Math.Sin(angle - wing)) * headSize;
        var right = to - new Vector(Math.Cos(angle + wing), Math.Sin(angle + wing)) * headSize;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(from, isFilled: false);
            context.LineTo(back);
            context.EndFigure(false);

            context.BeginFigure(left, isFilled: true);
            context.LineTo(to);
            context.LineTo(right);
            context.EndFigure(true);
        }

        var brush = _Brush("CockpitAccentBrush");
        return new Avalonia.Controls.Shapes.Path { Data = geometry, Stroke = brush, StrokeThickness = 2, Fill = brush, IsHitTestVisible = false };
    }

    // AC-907 val #2: this used to walk every visual descendant per call — quadratic across a whole selection change,
    // since _DrawFlowMarkers/_DrawNoteMarkers call it once per node. _ControlMap() is the same lookup, cached.
    private Control? _ControlFor(WireframeNode node) => _ControlMap().TryGetValue(node, out var control) ? control : null;

    // Changing the wording happens where the component is: a box over the component itself, Enter to keep it, Escape
    // to leave it as it was — the diagram's rename, one folder over.
    private void _StartTextEdit(WireframeNode? node)
    {
        if (node is null || _registry is null)
        {
            return;
        }

        // AC-914: a state has no control of its own on the canvas — RenderState never draws it as itself — so its
        // rename opens beside the chip that selected it rather than over a component that is not there.
        if (node.Kind == WireframeNodeKind.State)
        {
            if (node.Id is { } stateId && _stateChips.TryGetValue(stateId, out var chip))
            {
                _RenameStateViaFlyout(node, chip);
            }

            return;
        }

        if (_ControlFor(node) is not { } control || control.TranslatePoint(default, _surface) is not { } origin)
        {
            return;
        }

        _Select(node);
        if (_selectedId is not { } id)
        {
            return;
        }

        var box = new TextBox
        {
            Text = node.Text ?? "",
            MinWidth = Math.Max(120, control.Bounds.Width),
            FontSize = 13,
            Padding = new Thickness(4, 2),
        };
        Canvas.SetLeft(box, origin.X);
        Canvas.SetTop(box, origin.Y);
        _overlay.Children.Add(box);
        box.SelectAll();
        box.Focus();

        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _Apply(WireframeComponentEdit.SetText(id, box.Text ?? ""));
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
            }
        };
    }

    // AC-914: the state chip's own rename, a small Flyout anchored on the chip rather than the canvas overlay
    // _StartTextEdit uses for everything else — same Enter/Escape commit, no coordinate math needed.
    private void _RenameStateViaFlyout(WireframeNode state, Control anchor)
    {
        _Select(state);
        if (_selectedId is not { } id)
        {
            return;
        }

        var box = new TextBox { Text = state.Text ?? "", Width = 160 };
        var flyout = new Flyout { Content = new StackPanel { Margin = new Thickness(8), Children = { box } } };
        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                flyout.Hide();
                _Apply(WireframeComponentEdit.SetText(id, box.Text ?? ""));
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                flyout.Hide();
            }
        };
        flyout.ShowAt(anchor);
        box.Focus();
        box.SelectAll();
    }

    // A new component is named and typed as it is made, and lands either inside the selected container or straight
    // after the selected component — the two the format allows, offered as two buttons rather than guessed from where
    // the pointer was.
    private void _AddComponent(Control anchor)
    {
        if (_Selected is not { } target || _selectedId is not { } id)
        {
            return;
        }

        var chosen = WireframeNodeKind.Label;
        var palette = BuildPalette(kind => chosen = kind);
        var text = new TextBox { Width = 220, PlaceholderText = "Text (may be empty)" };
        var asChild = new Button { Content = "In this container", Classes = { "Compact" }, IsEnabled = target.IsContainer };
        var asSibling = new Button { Content = "Hieronder", Classes = { "Compact" }, IsEnabled = !_IsScreen(target) };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(12),
                Children =
                {
                    new ScrollViewer { MaxHeight = 340, Content = palette },
                    text,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { asChild, asSibling } },
                },
            },
        };

        void Add(bool child)
        {
            flyout.Hide();
            var keyword = WireframeHandEdit.Keyword(chosen);
            var wording = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text!.Trim();
            var edit = child
                ? WireframeHandEdit.AddChild(id, keyword, wording)
                : WireframeHandEdit.AddSibling(_screens, id, keyword, wording);
            if (edit is not null)
            {
                _Apply(edit);
            }
        }

        asChild.Click += (_, _) => Add(child: true);
        asSibling.Click += (_, _) => Add(child: false);
        flyout.ShowAt(anchor);
        text.Focus();
    }

    // Every keyword the format has apart from `screen`, in the five groups an operator thinks in (AC-903). A flat
    // list of 36 is a lookup; grouped, with the shape drawn beside the word, it is a choice.
    internal static readonly (string Group, Kind[] Kinds)[] Palette =
    [
        ("Layout", [Kind.Row, Kind.Column, Kind.Group, Kind.Card, Kind.Header, Kind.Footer, Kind.Sidebar, Kind.Main, Kind.Divider, Kind.Space]),
        ("Navigation", [Kind.Nav, Kind.Menu, Kind.Tabs, Kind.Tab, Kind.Breadcrumb, Kind.Pagination, Kind.Stepper, Kind.Item]),
        ("Input", [Kind.Input, Kind.Textarea, Kind.Search, Kind.Select, Kind.Checkbox, Kind.Radio, Kind.Toggle, Kind.Slider, Kind.Button]),
        ("Content", [Kind.Label, Kind.List, Kind.Table, Kind.Image, Kind.Avatar, Kind.Icon]),
        ("Feedback", [Kind.Modal, Kind.Badge, Kind.Progress]),
    ];

    internal static Control BuildPalette(Action<Kind> onPick)
    {
        var entries = new List<ToggleButton>();
        var stack = new StackPanel { Spacing = 4 };
        foreach (var (group, kinds) in Palette)
        {
            stack.Children.Add(new TextBlock { Text = group, FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) });
            var wrap = new WrapPanel { MaxWidth = 360 };
            foreach (var kind in kinds)
            {
                var entry = _PaletteEntry(kind);
                entry.IsChecked = kind == Kind.Label;
                entry.Click += (_, _) =>
                {
                    onPick(kind);
                    foreach (var other in entries)
                    {
                        other.IsChecked = ReferenceEquals(other, entry);
                    }
                };

                entries.Add(entry);
                wrap.Children.Add(entry);
            }

            stack.Children.Add(wrap);
        }

        return stack;
    }

    // The component itself, drawn small, rather than an icon standing in for it — the whiteboard's shape flyout does
    // the same thing one folder over, and for the same reason: this grid is recognised, not read.
    private static ToggleButton _PaletteEntry(Kind kind) => new()
    {
        Margin = new Thickness(2),
        Padding = new Thickness(4),
        Content = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new Viewbox
                {
                    Width = 56,
                    Height = 34,
                    Child = new Panel { Width = 132, Height = 80, Children = { WireframeRenderer.Render(_Sample(kind)) } },
                },
                new TextBlock
                {
                    Text = WireframeHandEdit.Keyword(kind),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            },
        },
    };

    // A component with enough in it to be recognisable at thumbnail size: containers get filler, the ones that hold
    // rows get rows, and a widget is its own preview.
    private static WireframeNode _Sample(Kind kind)
    {
        var node = new WireframeNode(kind, 0);
        if (!node.IsContainer)
        {
            return node;
        }

        var rows = kind is Kind.Nav or Kind.Menu or Kind.List or Kind.Table or Kind.Breadcrumb or Kind.Stepper;
        var child = rows ? Kind.Item : kind == Kind.Tabs ? Kind.Tab : Kind.Label;
        for (var index = 0; index < (rows ? 3 : 2); index++)
        {
            var sample = _Sample(child);
            sample.Modifiers.Add(new WireframeModifier(WireframeModifierName.W, "1", IsQuoted: false));
            node.Children.Add(sample);
        }

        return node;
    }

    private void _DeleteSelected()
    {
        if (_selectedId is { } id && _Apply(WireframeComponentEdit.Remove(id)))
        {
            _Select(null);
        }
    }

    // AC-901: a screen of its own beside the ones already there, added straight after the one being looked at. The
    // overview comes up with it, so the new board is where the operator can see it rather than off behind the one
    // they were in.
    private void _AddScreen()
    {
        var at = _ZoomedScreen is { } screen ? _screens.IndexOf(screen) + 1 : (int?)null;
        if (_Apply(WireframeComponentEdit.AddScreen(NewScreenTitle, at)))
        {
            _ShowOverview();
        }
    }

    private void _Reorder(int delta)
    {
        if (_selectedId is { } id && WireframeHandEdit.Reorder(_screens, id, delta) is { } edit)
        {
            _Apply(edit);
        }
    }

    // Into another container: the ones it can go into, named and numbered, rather than a drop target to aim at. With
    // more than one screen the destination says which screen it is on, so a move across screens is never a silent one.
    private void _MoveInto(Control anchor)
    {
        if (_selectedId is not { } id)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var destination in WireframeHandEdit.Destinations(_screens, id))
        {
            var item = new MenuItem { Header = $"{_Describe(destination)} — line {destination.Line}{_ScreenSuffix(destination)}" };
            var into = destination.Id!;
            item.Click += (_, _) => _Apply(WireframeComponentEdit.Move(id, into, position: null));
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            _host.ShowToast("There is no other container to place this component in.", PluginToastSeverity.Information);
            return;
        }

        flyout.ShowAt(anchor);
    }

    // One handling is one change towards the registry, under the same lock as the agent's — never a half state
    // written here and repaired afterwards. The re-render comes back through TextChanged.
    private bool _Apply(WireframeComponentEdit edit)
    {
        if (_registry is null)
        {
            return false;
        }

        // The per-component grammar words its refusals for the agent that normally calls it; the buttons above turn
        // the reachable ones off beforehand, so what gets through here is genuinely exceptional and worth showing raw.
        if (_registry.ApplyHandEdit(_surfaceId, edit) is not { } refusal)
        {
            return true;
        }

        _host.ShowToast(refusal, PluginToastSeverity.Warning);
        return false;
    }

    private void _RefreshHandEditBar()
    {
        var editable = _registry is not null;
        var target = _Selected;
        var isScreen = target is not null && _IsScreen(target);
        var placement = _selectedId is { } id ? WireframeHandEdit.Placement(_screens, id) : null;

        _addButton.IsEnabled = editable && target is not null;
        _textButton.IsEnabled = editable && target is not null;
        // AC-901: a screen goes the way any other component does, as long as it is not the last one left.
        _deleteButton.IsEnabled = editable && target is not null && (!isScreen || _screens.Count > 1);
        _upButton.IsEnabled = editable && placement is { Index: > 0 };
        _downButton.IsEnabled = editable && placement is { } at && at.Index < at.Parent.Children.Count - 1;
        _moveButton.IsEnabled = editable && target is not null && !isScreen;
        _addScreenButton.IsEnabled = editable;
        _overviewButton.IsVisible = _screens.Count > 1 && _ZoomedScreen is not null;
        _viewportButton.IsEnabled = editable;
        _viewportButton.Content = $"Viewport: {_canvasViewport}";

        // AC-910: asking works on the selection or on the wireframe as a whole (criterion 7), so the only real gate
        // is a live coupled session — same "explain at the point of use" rule as DiagramWorkspaceBody's ask button.
        _askButton.IsEnabled = _sessionBinding.IsLive;
        ToolTip.SetTip(
            _askButton,
            _sessionBinding.IsLive ? "Ask the agent about the selected component, or the whole wireframe."
            : "Couple a conversation first (\"Couple…\" above) to be able to ask the agent.");

        _handHint.Text = _HintFor(target);
        _RefreshPropertiesPanel(target, placement?.Parent.Kind);
        _RefreshStateStrip();
    }

    // AC-914: the state strip — Base plus one chip per state on the zoomed screen, and + State on a selected
    // container — rebuilt here alongside everything else _RefreshHandEditBar keeps in step, so it never drifts out
    // of sync with the toolbar's own enable-rule the way AC-924 warned a second enable-rule could (val #8).
    private void _RefreshStateStrip()
    {
        _stateChips.Clear();
        _stateStrip.Children.Clear();

        var states = _ZoomedScreen?.Children.Where(child => child.Kind == WireframeNodeKind.State).ToList() ?? [];
        _stateStrip.IsVisible = states.Count > 0;
        if (states.Count == 0)
        {
            return;
        }

        _stateStrip.Children.Add(_StateChip("Base", isOpen: _stateId is null, () => _SelectState(null)));
        foreach (var state in states)
        {
            var chip = _StateChip(state.Text ?? "State", isOpen: state.Id == _stateId, () => _SelectState(state));
            _stateStrip.Children.Add(chip);
            if (state.Id is { } id)
            {
                _stateChips[id] = chip;
            }
        }

        var add = new Button { Content = "+ State", Classes = { "Compact" }, IsEnabled = _CanAddState };
        ToolTip.SetTip(add, "A state for the selected container — empty, loading, error, whatever this screen needs.");
        add.Click += (_, _) => _AddState();
        _stateStrip.Children.Add(add);
    }

    private ToggleButton _StateChip(string label, bool isOpen, Action onClick)
    {
        var chip = new ToggleButton { Content = label, Classes = { "Compact" }, IsChecked = isOpen };
        chip.Click += (_, _) => onClick();
        return chip;
    }

    // AC-914 criterion 6: switching which state is open re-renders the screen and, per the grooming, also selects
    // the state itself — so Delete and «Text…» are of a piece with picking it, the same as clicking any component.
    //
    // AC-972: a state clicked for the first time carries no id yet (AC-906 mints lazily). EnsureComponentId stamps
    // the registry's copy of the source, not _sourceBox.Text, so rendering must pull the registry's (already
    // stamped) text rather than the stale local echo — else _RenderInto's "forgotten" guard erases it right back.
    private void _SelectState(WireframeNode? state)
    {
        _stateId = state is null ? null : _registry?.EnsureComponentId(_surfaceId, state.Line) ?? state.Id;
        _RenderInto(_registry?.PeekText(_surfaceId) ?? _sourceBox.Text ?? "");
        _Select(state);
    }

    // AC-914 criterion 7: a container within this screen, not the screen itself and not another state — the same
    // container the properties panel would let the operator pick a replaces: target from, if there were one.
    private bool _CanAddState =>
        _registry is not null && _ZoomedScreen is not null && _Selected is { } target
        && target.IsContainer && !_IsScreen(target) && target.Kind != WireframeNodeKind.State;

    private void _AddState()
    {
        if (_ZoomedScreen is not { } screen || screen.Id is not { } screenId || _selectedId is not { } targetId)
        {
            return;
        }

        _Apply(WireframeComponentEdit.Add(screenId, "state", NewStateTitle, $"replaces:#{targetId}", null));
    }

    // AC-910: asks the coupled session about the selection (or, with nothing selected, the wireframe as a whole) —
    // this surface's descriptor is the component's own stable #id plus which screen it is on, since AC-901 made
    // "the button" ambiguous without one.
    private void _AddAsk(Control anchor)
    {
        if (!_sessionBinding.IsLive)
        {
            return;
        }

        var target = _Selected;
        var screen = target is not null ? WireframeHandEdit.ScreenOf(_screens, target) : null;
        var context = new AskContext(
            "wireframe",
            _surfaceId,
            _documentTitle,
            _selectedId is { } id ? $"#{id}" : null,
            screen is not null ? $"on screen \"{screen.Text}\"" : null);

        AskFlyout.Show(anchor, "What should the agent do here?", question =>
        {
            _askStrip.Add(question, _selectedId);
            _ = _sessionBinding.SendAsync(AskMessage.Compose(context, question));
        });
    }

    // AC-910: an ask entry's row jumps to the screen a component lives on and to the component itself. Looked up
    // twice — _ZoomInto redraws from source, which hands back a fresh tree (ids survive that, node instances don't).
    private void _JumpToComponent(string componentId)
    {
        if (WireframeHandEdit.Find(_screens, componentId) is not { } node)
        {
            _host.ShowToast("That component is no longer on this wireframe.", PluginToastSeverity.Information);
            return;
        }

        if (WireframeHandEdit.ScreenOf(_screens, node) is { } screen)
        {
            _ZoomInto(screen);
        }

        if (WireframeHandEdit.Find(_screens, componentId) is { } stillThere)
        {
            _Select(stillThere);
        }
    }

    // English, as every user-facing string in the cockpit is (Raymond 2026-07-05). The four here were Dutch and are
    // converted along with the one AC-904 had to reword, so the line does not read half in each language.
    private string _HintFor(WireframeNode? target)
    {
        if (_ZoomedScreen is null)
        {
            return target is null
                ? "Double-click a screen to step into it."
                : $"{_Describe(target)} on line {target.Line} — double-click to step into this screen.";
        }

        return target is null
            ? "Click a component to edit it."
            : $"{_Describe(target)} on line {target.Line}{_StateScope(target)} — drag to move it, double-click to change its wording.";
    }

    // AC-914 criterion 9: while a state is open, whether the selected component belongs to it or to the base
    // screen — said before the operator edits it, since a base edit shows in every state and a state edit does not.
    private string _StateScope(WireframeNode target)
    {
        if (_OpenState is not { } state)
        {
            return "";
        }

        return ReferenceEquals(target, state) || WireframeHandEdit.Find(state, target.Line) is not null
            ? " (in the open state)"
            : " (in the base screen, visible in every state)";
    }

    private bool _IsScreen(WireframeNode node) => _screens.Contains(node);

    // Which screen a destination is on, said out loud only when the document has more than one — with a single
    // screen it is noise on every line of the menu.
    private string _ScreenSuffix(WireframeNode node) =>
        _screens.Count > 1 && WireframeHandEdit.ScreenOf(_screens, node) is { } screen
            ? $" · screen «{screen.Text}»"
            : "";

    // ---- Properties panel (AC-905): the operator's way to set the same modifiers the agent could always set ----

    // A fixed column rather than a flyout: it stays put across a run of selections instead of reopening every click,
    // which is calmer with the toolbar/coupling bar/presence/activity strip already docked around the same window.
    // AC-907: the numbered notes, in a column of their own above the properties panel — never on the canvas (see the
    // grooming on this ticket: fit-zoom and the overlay's own sweep both work against a list living there).
    private static (Border Panel, StackPanel Content) _BuildNotesPanel()
    {
        var content = new StackPanel { Spacing = 6 };
        var panel = new Border
        {
            Width = 240,
            MaxHeight = 220,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1, 0, 0, 1),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            IsVisible = false,
            Child = new ScrollViewer { Content = content },
        };
        return (panel, content);
    }

    // Rebuilt from the tree rather than the overlay's own drawing pass: unlike the markers, this needs no measured
    // layout, so it can run the moment the source changes instead of waiting on the next Loaded dispatch.
    private void _RefreshNotesPanel()
    {
        _notesContent.Children.Clear();
        var groups = _NotesVisible ? _NoteGroups() : new List<(WireframeNode Screen, List<WireframeNode> Notes)>();
        _notesPanel.IsVisible = groups.Count > 0;

        foreach (var (screen, notes) in groups)
        {
            if (_ZoomedScreen is null)
            {
                _notesContent.Children.Add(new TextBlock
                {
                    Text = screen.Text,
                    FontWeight = FontWeight.Bold,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            for (var index = 0; index < notes.Count; index++)
            {
                var node = notes[index];
                var selected = ReferenceEquals(node, _Selected);
                var row = new Border
                {
                    Padding = new Thickness(4, 2),
                    CornerRadius = new CornerRadius(3),
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Background = selected ? _Brush("CockpitAccentSelectionBrush") : null,
                    Child = new TextBlock
                    {
                        Text = $"{_Numbered(index + 1)} {node.ValueOf(WireframeModifierName.Note)}",
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                row.PointerPressed += (_, _) => _Select(node);
                _notesContent.Children.Add(row);
            }
        }
    }

    private static (Border Panel, StackPanel Content) _BuildPropertiesPanel()
    {
        var content = new StackPanel { Spacing = 10 };
        var panel = new Border
        {
            Width = 240,
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new ScrollViewer { Content = content },
        };
        return (panel, content);
    }

    // AC-905 AC6: nothing at all with no selection — the toolbar's own hint already says to click a component — and
    // for the screen line only what WireframeModifierRules says applies there (disabled + align, nothing else).
    private void _RefreshPropertiesPanel(WireframeNode? node, Kind? parentKind)
    {
        _propertiesContent.Children.Clear();
        if (node is null || _selectedId is not { } id)
        {
            return;
        }

        _propertiesContent.Children.Add(new TextBlock
        {
            Text = _Describe(node),
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var flag in FlagModifiers)
        {
            if (WireframeModifierRules.Applies(node.Kind, parentKind, flag))
            {
                _propertiesContent.Children.Add(_BuildFlagCheckbox(node, id, flag));
            }
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.W))
        {
            _propertiesContent.Children.Add(_BuildWeightPicker(node, id, WireframeModifierName.W, "Width (w:)"));
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.H))
        {
            _propertiesContent.Children.Add(_BuildWeightPicker(node, id, WireframeModifierName.H, "Height (h:)"));
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.Align))
        {
            _propertiesContent.Children.Add(_BuildAlignPicker(node, id));
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.Value))
        {
            _propertiesContent.Children.Add(_BuildValueField(node, id));
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.Goto))
        {
            _propertiesContent.Children.Add(_BuildGotoPicker(node, id));
        }

        if (WireframeModifierRules.Applies(node.Kind, parentKind, WireframeModifierName.Note))
        {
            _propertiesContent.Children.Add(_BuildNoteField(node, id));
        }
    }

    private static readonly WireframeModifierName[] FlagModifiers =
        [WireframeModifierName.Primary, WireframeModifierName.Selected, WireframeModifierName.Checked, WireframeModifierName.Disabled];

    private static string _FlagLabel(WireframeModifierName name) => name switch
    {
        WireframeModifierName.Primary => "Primary",
        WireframeModifierName.Selected => "Selected",
        WireframeModifierName.Checked => "Checked",
        _ => "Disabled",
    };

    private CheckBox _BuildFlagCheckbox(WireframeNode node, string id, WireframeModifierName name)
    {
        var box = new CheckBox { Content = _FlagLabel(name), IsChecked = node.Has(name) };
        box.IsCheckedChanged += (_, _) => _Apply(WireframeComponentEdit.ToggleModifier(id, name, box.IsChecked == true));
        return box;
    }

    // AC-905 AC3: `w:`/`h:` are a flex ratio, never pixels — said in words here rather than left for the operator to
    // find out by seeing two boxes of very different sizes and guessing why.
    private StackPanel _BuildWeightPicker(WireframeNode node, string id, WireframeModifierName name, string label)
    {
        var combo = new ComboBox { ItemsSource = WeightChoices, SelectedItem = node.WeightOf(name)?.ToString() ?? NoWeight, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            var chosen = combo.SelectedItem as string;
            _Apply(WireframeComponentEdit.SetModifier(id, name, chosen == NoWeight ? null : chosen));
        };

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = label, FontSize = 11 },
                combo,
                new TextBlock
                {
                    Text = "A share of the space, not a size in pixels — w:2 beside w:1 takes twice as much.",
                    FontSize = 10,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    private const string NoWeight = "—";

    private static readonly string[] WeightChoices = [NoWeight, "1", "2", "3", "4", "5", "6"];

    private StackPanel _BuildAlignPicker(WireframeNode node, string id)
    {
        var combo = new ComboBox { ItemsSource = AlignChoices, SelectedItem = node.Alignment?.ToString().ToLowerInvariant() ?? NoAlign, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            var chosen = combo.SelectedItem as string;
            _Apply(WireframeComponentEdit.SetModifier(id, WireframeModifierName.Align, chosen == NoAlign ? null : chosen));
        };

        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = "Align", FontSize = 11 }, combo },
        };
    }

    private const string NoAlign = "—";

    private static readonly string[] AlignChoices = [NoAlign, "left", "center", "right"];

    // AC-902 (WF-2/WF-6): the operator's way to lay a flow without typing the source — a picker of the document's
    // own screen titles, the current screen left out since a board never points at itself in the overview.
    private StackPanel _BuildGotoPicker(WireframeNode node, string id)
    {
        var own = WireframeHandEdit.ScreenOf(_screens, node);
        var choices = new[] { NoGoto }.Concat(_screens.Where(screen => screen != own).Select(screen => screen.Text ?? "")).ToArray();
        var combo = new ComboBox { ItemsSource = choices, SelectedItem = node.ValueOf(WireframeModifierName.Goto) ?? NoGoto, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            var chosen = combo.SelectedItem as string;
            _Apply(WireframeComponentEdit.SetModifier(id, WireframeModifierName.Goto, chosen == NoGoto ? null : chosen, quoted: true));
        };

        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = "Goes to", FontSize = 11 }, combo },
        };
    }

    private const string NoGoto = "—";

    // AC-905 AC1: `value:` on a slider/progress/pagination is a 0-100 number the format reads back unquoted; every
    // other component takes free text, quoted like the writer already quotes any other text on the line.
    private StackPanel _BuildValueField(WireframeNode node, string id)
    {
        var numeric = WireframeModifierRules.ValueIsNumeric(node.Kind);
        var box = new TextBox
        {
            Text = node.ValueOf(WireframeModifierName.Value) ?? "",
            PlaceholderText = numeric ? "0-100" : "",
        };

        void Commit()
        {
            var value = box.Text ?? "";
            _Apply(WireframeComponentEdit.SetModifier(id, WireframeModifierName.Value, value.Length == 0 ? null : value, quoted: !numeric));
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Commit();
            }
        };

        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = "Value", FontSize = 11 }, box },
        };
    }

    // AC-907 AC5: the operator's way to set/change/clear a note without typing the source — modelled on
    // _BuildValueField. One line per component (AC-907's own limit), so no AcceptsReturn.
    private StackPanel _BuildNoteField(WireframeNode node, string id)
    {
        var box = new TextBox
        {
            Text = node.ValueOf(WireframeModifierName.Note) ?? "",
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };

        void Commit()
        {
            var value = box.Text ?? "";
            _Apply(WireframeComponentEdit.SetModifier(id, WireframeModifierName.Note, value.Length == 0 ? null : value, quoted: true));
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Commit();
            }
        };

        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = "Note", FontSize = 11 }, box },
        };
    }

    // A component named the way the operator reads it: "input «E-mailadres»", or the bare keyword when it carries no
    // text of its own.
    private static string _Describe(WireframeNode node) =>
        string.IsNullOrEmpty(node.Text) ? WireframeHandEdit.Keyword(node.Kind) : $"{WireframeHandEdit.Keyword(node.Kind)} «{node.Text}»";

    private void _ZoomByButton(double factor) =>
        _ZoomAround(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2), _zoom * factor, _Selected);

    private void _ZoomAround(Point anchor, double requestedZoom, WireframeNode? under)
    {
        (_zoom, _panOffset) = DiagramZoomMath.ZoomAround(anchor, _panOffset, _zoom, requestedZoom, MinZoom, MaxZoom);
        _isFitMode = false;
        if (_SwitchedView(under))
        {
            return;
        }

        _ApplyTransform();
    }

    // AC-901: the zoom level is the third way between the views. Past the level at which one board would fill the
    // window you are inside that screen; back below the level at which the whole overview fits you see the set again.
    // The overview canvas is always the larger of the two, so the thresholds cannot chase each other.
    private bool _SwitchedView(WireframeNode? under)
    {
        if (_ZoomedScreen is null)
        {
            if (_zoom <= _FitZoomFor(_ScreenSize)
                || under is null
                || WireframeHandEdit.ScreenOf(_screens, under) is not { } screen)
            {
                return false;
            }

            _ZoomInto(screen);
            return true;
        }

        if (_screens.Count < 2 || _zoom > _FitZoomFor(WireframeRenderer.OverviewSize(_screens.Count, _ScreenSize)))
        {
            return false;
        }

        _ShowOverview();
        return true;
    }

    // "Passend maken": recomputed from the viewport's own SizeChanged (first layout, then every resize), so the
    // first render lands at true size and keeps filling the window across a move/resize (AC-873's survive-resize AC).
    private void _ApplyFit()
    {
        _isFitMode = true;
        var canvas = _CanvasSize;
        var fitZoom = _FitZoomFor(canvas);
        if (fitZoom <= 0)
        {
            return;
        }

        _zoom = fitZoom;
        _panOffset = DiagramZoomMath.CenteredPanOffset(_viewport.Bounds.Size, canvas, _zoom);
        _ApplyTransform();
    }

    private double _FitZoomFor(Size canvas) => DiagramZoomMath.FitZoom(_viewport.Bounds.Size, canvas, MinZoom, MaxZoom);

    private void _ApplyTransform()
    {
        _surface.RenderTransform = new MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _panOffset.X, _panOffset.Y));
        _zoomLabel.Text = $"{_zoom * 100:0}%";
    }

    // AC-811: the wireframe source is one click away — collapsed under the render, never only in memory. Always
    // read-only, AC-875 included: the source stays the truth and is rebuilt from each handling, so an edit goes
    // through the registry's per-component path where the journal and the "you're editing" hold both see it.
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
        var toggle = new ToggleButton { Content = "Show source", Classes = { "Compact" }, Margin = new Thickness(8, 4) };
        toggle.IsCheckedChanged += (_, _) => box.IsVisible = toggle.IsChecked == true;
        return (toggle, box);
    }

    private (Border Toolbar, TextBlock ZoomLabel, Button Save, TextBlock SaveStatus, Button Add, Button Text,
        Button Delete, Button Up, Button Down, Button Move, Button AddScreen, StackPanel StateStrip, Button Overview,
        Button Viewport, Button Ask, ToggleButton Notes, TextBlock Hint) _BuildToolbar()
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

        // AC-874/WF-4: where this wireframe lives, beside the button that puts it there — DiagramWorkspaceBody's
        // Opslaan, one folder over. "No file yet" is a state the window shows just as well as a path.
        var save = new Button { Content = "Save", Classes = { "Compact" } };
        save.Click += (_, _) => _ = _SaveAsync();
        var saveStatus = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        // AC-875: what the operator clicked on the surface is what these buttons work on. Moving also lives on the
        // surface since AC-904 — the arrows and "Move to…" stay for naming a destination rather than aiming at
        // one, which is the shorter way across a screen and the only way with the keyboard.
        var add = new Button { Content = "+ Component…", Classes = { "Compact" } };
        add.Click += (_, _) => _AddComponent(add);
        var text = new Button { Content = "Text…", Classes = { "Compact" } };
        text.Click += (_, _) => _StartTextEdit(_Selected);
        var delete = new Button { Content = "Delete", Classes = { "Compact" } };
        delete.Click += (_, _) => _DeleteSelected();
        var up = new Button { Content = "↑", Classes = { "Compact" }, MinWidth = 28 };
        ToolTip.SetTip(up, "One place up within the same container.");
        up.Click += (_, _) => _Reorder(-1);
        var down = new Button { Content = "↓", Classes = { "Compact" }, MinWidth = 28 };
        ToolTip.SetTip(down, "One place down within the same container.");
        down.Click += (_, _) => _Reorder(1);
        var move = new Button { Content = "Move to…", Classes = { "Compact" } };
        move.Click += (_, _) => _MoveInto(move);
        // AC-901: a wireframe holds as many screens as the thing it sketches has, so adding one is a button rather
        // than a second file, and the way back out of a screen stands beside it.
        var addScreen = new Button { Content = "+ Screen", Classes = { "Compact" } };
        ToolTip.SetTip(addScreen, "One more screen, alongside the ones already there.");
        addScreen.Click += (_, _) => _AddScreen();
        // AC-914: Base plus one chip per state, populated by _RefreshStateStrip — empty and invisible until the
        // zoomed screen actually has states, the same on-demand shape as _overviewButton below.
        var stateStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        var overview = new Button { Content = "← Overview", Classes = { "Compact" }, IsVisible = false };
        ToolTip.SetTip(overview, "All screens side by side.");
        overview.Click += (_, _) => _ShowOverview();
        // AC-915: the operator's way to switch between the three sheet sizes without typing the source — its own
        // caption is the current viewport, so this doubles as the AC4 "what am I looking at" readout.
        var viewport = new Button { Classes = { "Compact" } };
        ToolTip.SetTip(viewport, "The sheet size everything is measured against — desktop, tablet or mobile.");
        viewport.Click += (_, _) => _ShowViewportMenu(viewport);
        // AC-910: the operator's free-text ask about the selection (or, with nothing selected, the wireframe as a
        // whole), sent to the coupled session the moment it is submitted — see _AddAsk.
        var ask = new Button { Content = "Ask the agent…", Classes = { "Compact" } };
        ask.Click += (_, _) => _AddAsk(ask);
        // AC-907 criterion 8: one switch for both the markers and the list beside them — on by default, since a
        // hidden note staying hidden is exactly the case AC-907 exists to rule out (see the grooming's §9.2).
        var notes = new ToggleButton { Content = "Notes", Classes = { "Compact" }, IsChecked = true };
        ToolTip.SetTip(notes, "Show or hide the numbered requirement notes.");
        notes.IsCheckedChanged += (_, _) =>
        {
            _RefreshOverlay();
            _RefreshNotesPanel();
        };
        var hint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        var handEditControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { overview, add, text, delete, up, down, move, addScreen, stateStrip, viewport, ask, notes, save, saveStatus, hint },
        };

        var bar = new DockPanel { Children = { handEditControls, zoomControls } };
        DockPanel.SetDock(handEditControls, Dock.Left);
        DockPanel.SetDock(zoomControls, Dock.Right);
        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel, save, saveStatus,
            add, text, delete, up, down, move, addScreen, stateStrip, overview, viewport, ask, notes, hint);
    }

    // AC-915: three names, no free-form size — the same MenuFlyout shape as «Move to…». Landing on the viewport
    // already chosen does nothing (AC-915's own no-op guard, mirroring what the editor would refuse anyway).
    private void _ShowViewportMenu(Control anchor)
    {
        var flyout = new MenuFlyout();
        foreach (var candidate in Enum.GetValues<WireframeViewport>())
        {
            var item = new MenuItem { Header = candidate.ToString() };
            item.Click += (_, _) =>
            {
                if (candidate != _canvasViewport)
                {
                    _Apply(WireframeComponentEdit.SetViewport(candidate));
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    // Eén opslagweg (AC-839's precedent, one folder over): the source box always mirrors the surface's current
    // text — an agent's edit_wireframe and the operator's own handling (AC-875) both arrive through TextChanged — so
    // "unsaved changes" is the same comparison for both.
    private async Task _SaveAsync()
    {
        if (_filePath is { } existing)
        {
            _Persist(text =>
            {
                WireframeCatalog.Write(existing, _documentTitle, text, _fileAsLastSeen);
                return existing;
            });
            return;
        }

        var homes = WireframeCatalog.WritableHomes(await _host.GetProjectMemoryRowsAsync(_sessionBinding.LivePaneId));
        if (homes.Count == 0)
        {
            _host.ShowToast(
                "This project has no memory path — add one in the project editor before saving a wireframe.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(text => WireframeCatalog.Create(homes[0].Reference, _documentTitle, text));
            return;
        }

        // More than one memory path: ask, never pick one (AC-812). The answer stays with this wireframe.
        var flyout = new MenuFlyout();
        foreach (var home in homes)
        {
            var item = new MenuItem { Header = home.Label ?? home.Reference };
            item.Click += (_, _) => _Persist(text => WireframeCatalog.Create(home.Reference, _documentTitle, text));
            flyout.Items.Add(item);
        }

        flyout.ShowAt(_saveButton);
    }

    // The writer only says where it landed; the bookkeeping and the one error path live here.
    private void _Persist(Func<string, string> write)
    {
        var text = _sourceBox.Text ?? "";
        try
        {
            _filePath = write(text);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Save failed: {exception.Message}", PluginToastSeverity.Error);
            return;
        }

        _savedText = text;
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);
        _RefreshSaveBar();
    }

    private void _RefreshSaveBar()
    {
        var dirty = (_sourceBox.Text ?? "") != _savedText;
        var where = _filePath ?? "No file yet";
        _saveStatus.Text = dirty ? $"{where} · unsaved changes" : where;
        ToolTip.SetTip(_saveStatus, _filePath);
        _saveButton.IsEnabled = dirty || _filePath is null;
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
                ? $"Session {ended} has ended — this window stays open."
                : "No agent coupled.";
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
