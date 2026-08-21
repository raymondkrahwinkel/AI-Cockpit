using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Svg.Skia;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Diagrams;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Whiteboard.Canvas;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Material.Icons;
using Material.Icons.Avalonia;
using Mermaider;

namespace Cockpit.Plugin.Diagram;

// The whole body of a diagram window (AC-809 proved the panel survives the plugin boundary; AC-810 wired the
// cockpit-diagram MCP coupling; AC-834 makes it a window beside the cockpit, bound to a session that is already
// running). It starts nothing and ends nothing: the conversation stays in the session, the binding is a peephole.
internal sealed class DiagramWorkspaceBody : UserControl
{
    // AC-837 zoom/pan range and wheel feel, same shape as ImagePreviewWindow's image zoom.
    private const double MinZoom = 0.1;
    private const double MaxZoom = 8.0;
    private const double WheelZoomStepBase = 1.15;
    private const double ButtonZoomStep = 1.25;

    // A press that travels less than this is a click on an object, not a pan (AC-841: no gesture has to be guessed
    // apart from another).
    private const double ClickSlopPx = 4;

    private static readonly Cursor _PanCursor = new(StandardCursorType.Hand);
    private static readonly Cursor _PanningCursor = new(StandardCursorType.SizeAll);

    private readonly ICockpitHost _host;
    private readonly IDiagramAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly string _documentTitle;
    private readonly Avalonia.Svg.Skia.Svg _svg;
    private readonly Canvas _overlay;
    private readonly Panel _surface;
    private readonly Border _viewport;
    // AC-978: reuses the whiteboard's own overlay so a blank diagram gives the same "here's what to do" hint,
    // instead of the near-zero SVG bounds silently clamping the fit zoom to 800% over nothing.
    private readonly WhiteboardCanvasControl.EmptyStateOverlay _emptyState =
        new(_EmptyStateMessage(sessionIsLive: false)) { IsHitTestVisible = false };
    private readonly TextBlock _zoomLabel;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private readonly TextBlock _editChip;
    private readonly Button _coupleButton;
    private readonly Button _disconnectButton;
    private readonly Border _proposalPanel;
    private readonly ToggleButton _sourceToggle;
    private readonly TextBox _sourceBox;
    private readonly ActivityStrip _activityStrip;
    private readonly AskStrip _askStrip;
    private readonly PresenceIndicators _presence;
    private readonly ToggleButton _followToggle;
    private readonly Button _saveButton;
    private readonly TextBlock _saveStatus;
    private string? _filePath;
    private string _savedText;
    private string? _fileAsLastSeen;
    private string _currentSvg = "";
    private double _zoom = 1.0;
    private Vector _panOffset;
    private Size _diagramSize;
    private bool _isFitMode = true;
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;
    private DiagramProposal? _pendingProposal;
    private readonly HashSet<int> _acceptedBlocks = [];
    private readonly Button _addButton;
    private readonly Button _connectButton;
    private readonly Button _renameButton;
    private readonly Button _deleteButton;
    private readonly Button _attributesButton;
    private readonly Button _shapeButton;
    private readonly Button _askButton;
    private readonly TextBlock _handHint;
    private readonly TextBlock _hintSeparator;
    private DiagramEditSupport _support = new(DiagramEditDialect.Flowchart, null);
    private IReadOnlyList<DiagramObjectAt> _objects = [];
    private DiagramObjectAt? _selected;
    private string? _connectFrom;
    private bool _isConnecting;
    private bool _placementHintShown;
    private double _svgScale = 1;
    private DiagramObjectAt? _pressedOn;
    private SurfaceSessionBinding _sessionBinding;
    private string? _agentCursorKey;
    private bool _glowActive;
    private int _glowGeneration;
    private bool _following;

    public DiagramWorkspaceBody(ICockpitHost host, DiagramDocument document, string? sessionPaneId)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _surfaceId = document.Id;
        _documentTitle = document.Title;
        _filePath = document.FilePath;
        _savedText = document.MermaidText;
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);

        // AC-837: no fixed size and no ScrollViewer. Avalonia.Svg.Skia.Svg's own measure gives a placeholder size
        // before its picture is ready, so `_RenderInto` reads the real size off the Skia picture instead, and
        // `_viewport` positions/scales the control itself via RenderTransform for zoom and pan.
        _svg = new Avalonia.Svg.Skia.Svg(baseUri: null!)
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // AC-841: selection, the "you're editing" marking and the rename box sit on their own canvas above the render, in
        // the SVG's own coordinates — so zoom and pan move them with the picture rather than beside it. No background,
        // so only the rename box itself takes the pointer; the marks let a click through to the diagram under them.
        _overlay = new Canvas();
        _surface = new Panel
        {
            Children = { _svg, _overlay },
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
        };
        _viewport = _BuildViewport();

        (_couplingBar, _couplingLabel, _readChip, _editChip, _coupleButton, _disconnectButton) = _BuildCouplingBar();
        _proposalPanel = _BuildProposalPanel();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        (var toolbar, _zoomLabel, _saveButton, _saveStatus, _addButton, _connectButton, _renameButton, _deleteButton, _attributesButton, _shapeButton, _askButton, _handHint, _followToggle, _hintSeparator) = _BuildToolbar();
        var diagramJournal = new DiagramActivityJournal(_registry);
        _activityStrip = new ActivityStrip(host, _surfaceId, diagramJournal, key => _ = _FlashObjectAsync(key));
        _askStrip = new AskStrip(key => _ = _FlashObjectAsync(key));
        _presence = new PresenceIndicators(_surfaceId, diagramJournal, diagramJournal);

        Content = new DockPanel
        {
            Children = { toolbar, _couplingBar, _presence, _proposalPanel, _sourceToggle, _sourceBox, _askStrip, _activityStrip, _viewport },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_proposalPanel, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);
        DockPanel.SetDock(_askStrip, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);

        // AC-834: the session is named by whoever opened this window, never guessed — a not-live binding is the
        // "no agent on this diagram" state. Bound before the first _RenderInto (AC-849): its _RefreshHandEditBar
        // reads _sessionBinding.IsLive for the ask button, refreshed by the same coupling-change callback.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, () => { _RefreshCouplingBar(); _RefreshHandEditBar(); });
        _RenderInto(document.MermaidText);
        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);

        if (_registry is not null)
        {
            // Subscribed before the surface is registered: a window an agent asked for (AC-835) arrives already
            // coupled, and that change is announced from inside SurfaceOpened.
            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.TextChanged += _OnTextChanged;
            _registry.ProposalChanged += _OnProposalChanged;
            _registry.HistoryChanged += _OnHistoryChanged;
            _registry.SurfaceOpened(_surfaceId, document.Title, document.MermaidText);

            // AC-899: which hand-edit controls belong on this diagram is the registry's answer about the surface,
            // so it can only be asked once the surface is registered — the first _RenderInto ran before that.
            _RefreshEditSupport();

            // A plain Couple — zero capabilities. read_diagram/edit_diagram still ask their own consent (AC-810).
            if (_sessionBinding.IsLive)
            {
                _registry.Couple(_sessionBinding.PaneId, _surfaceId);
            }
        }

        // No registry (an older host) means coupling cannot be shown or offered at all, so the bar goes rather
        // than standing there with a Couple… button that could do nothing.
        _couplingBar.IsVisible = _registry is not null;
        _RefreshCouplingBar();

        DetachedFromVisualTree += (_, _) =>
        {
            _sessionBinding.Dispose();
            if (_registry is null)
            {
                return;
            }

            if (_selected is { } stillHeld)
            {
                _registry.ReleaseObject(_surfaceId, stillHeld.HoldKey);
            }

            _registry.CouplingChanged -= _OnCouplingChanged;
            _registry.TextChanged -= _OnTextChanged;
            _registry.ProposalChanged -= _OnProposalChanged;
            _registry.HistoryChanged -= _OnHistoryChanged;
            _registry.SurfaceClosed(_surfaceId);
        };
    }

    // Couples this diagram to another running session — the way out of "window open, no agent", after the bound
    // session ended or the operator disconnected. Exclusivity is the registry's (IsCoupledByAnother): a surface a
    // different agent already holds refuses, and the operator is told rather than shown an exception.
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
        _RefreshHandEditBar();
    }

    // ListSurfaces/CouplingOf are session-scoped (AC-89: an agent only sees its own coupling) — this panel is not
    // an agent session, so it has no session id to query with. Tracked from the change event instead.
    private DiagramCoupling? _current;

    private void _OnCouplingChanged(DiagramCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        _current = change.Coupling;
        if (_current is null)
        {
            // Same "absent, not empty-but-present" rule as the coupling bar: no coupling means no agent cursor
            // either, whatever it was pointing at.
            _agentCursorKey = null;
            _glowActive = false;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _RefreshCouplingBar();
            _RefreshOverlay();
        });
    }

    // AC-847: the agent's cursor on the surface — the last non-operator, non-reverted edit's object, marked and
    // briefly glowing while it is fresh (see _PulseGlowAsync), then settling into a quieter persistent outline.
    private void _OnHistoryChanged(string surfaceId)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var last = _registry?.History(_surfaceId).LastOrDefault();
            if (last is not { Origin: not "operator", Reverted: false } entry)
            {
                return;
            }

            _agentCursorKey = entry.ObjectKey;
            _RefreshOverlay();
            _ = _PulseGlowAsync();

            if (_following)
            {
                _FollowTo(entry.ObjectKey);
            }
        });
    }

    // Restarts on every fresh edit via a generation counter (mirrors _FlashObjectAsync's fire-and-forget shape) so
    // an older timer landing late can never clear a glow a newer edit just started.
    private async Task _PulseGlowAsync()
    {
        var myGeneration = ++_glowGeneration;
        _glowActive = true;
        _RefreshOverlay();
        await Task.Delay(3000);
        if (_glowGeneration == myGeneration)
        {
            _glowActive = false;
            _RefreshOverlay();
        }
    }

    // AC-847's Follow: pan (never zoom) so the agent's just-edited object lands in the viewport's own centre, using
    // whatever zoom level is already set. Cancelled the moment the operator pans or zooms by hand — see
    // _OnViewportWheel/_OnViewportPointerMoved.
    private void _FollowTo(string objectKey)
    {
        var target = _Locate(objectKey);
        if (target is null)
        {
            return;
        }

        var center = new Point(
            (target.Bounds.X + target.Bounds.Width / 2) * _svgScale,
            (target.Bounds.Y + target.Bounds.Height / 2) * _svgScale);
        var viewportCenter = _viewport.Bounds.Size;
        _panOffset = new Vector(viewportCenter.Width / 2, viewportCenter.Height / 2) - new Vector(center.X, center.Y) * _zoom;
        _ApplyTransform();
    }

    private void _OnTextChanged(string surfaceId, string text)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => _RenderInto(text));
    }

    // AC-825: an edit_diagram delivery lands here as a proposal, not as a fait accompli — the surface's rendered
    // source only changes once ResolveProposal writes it (which raises TextChanged separately, above).
    private void _OnProposalChanged(string surfaceId, DiagramProposal? proposal)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _pendingProposal = proposal;
            _acceptedBlocks.Clear();
            _RefreshProposalPanel();
        });
    }

    // AC-911: opens the same template list the quick-start dialog offers — one "give me an example" path, two
    // entrances. Replaces whatever is on the surface, same as an agent's edit_diagram would, so it goes through
    // UpdateText and reaches any coupled agent as well.
    private async Task _InsertTemplateAsync()
    {
        SurfaceTemplate? picked = null;

        await _host.ShowDialogAsync("Insert template", () =>
        {
            var (strip, getSelected) = SurfaceTemplateStrip.Build(DiagramTemplates.All, DiagramTemplates.Preview);

            var insert = new Button { Content = "Insert", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Right };
            insert.Click += (sender, _) =>
            {
                picked = getSelected();
                (sender as Control)?.FindAncestorOfType<Window>()?.Close();
            };
            var cancel = new Button { Content = "Cancel", Classes = { "Ghost" }, Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Right };
            cancel.Click += (sender, _) => (sender as Control)?.FindAncestorOfType<Window>()?.Close();

            var footer = new Border
            {
                Padding = new Thickness(14, 11),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = SurfaceChrome.Brush("CockpitHairlineBrush"),
                [DockPanel.DockProperty] = Dock.Bottom,
                Child = new DockPanel { LastChildFill = false, Children = { insert, cancel } },
            };
            var body = new StackPanel { Margin = new Thickness(16, 14), Children = { new ScrollViewer { MaxHeight = 320, Content = strip } } };

            return new DockPanel { LastChildFill = true, Children = { footer, body } };
        }, "diagram.template", width: 460, height: 420);

        if (picked is not { } template)
        {
            return;
        }

        _RenderInto(template.Source);
        _registry?.UpdateText(_surfaceId, template.Source);
    }

    private void _RenderInto(string source)
    {
        // Straight from Mermaider, no CssFlattener step: measured (AC-809) that Svg.Controls.Skia.Avalonia's own
        // CSS engine already resolves the var()/color-mix() this emits, and that CssFlattener's output renders
        // worse, not better — a separately tracked regression (AC-819), not this ticket's concern.
        var markup = MermaidRenderer.RenderSvg(source, DiagramTheme.Options);
        _currentSvg = markup;
        _svg.SvgSource = SvgSource.LoadFromSvg(markup);
        _sourceBox.Text = source;

        // The real fix for the old 340x200 workaround: the picture's own bounds, read straight off Skia, not off
        // Avalonia's (buggy) first measure pass — true size from the first render, no second interaction needed.
        var bounds = _svg.SvgSource?.Picture?.CullRect;
        _diagramSize = bounds is { Width: > 0, Height: > 0 } rect ? new Size(rect.Width, rect.Height) : new Size(340, 200);
        _svg.Width = _diagramSize.Width;
        _svg.Height = _diagramSize.Height;
        _surface.Width = _overlay.Width = _diagramSize.Width;
        _surface.Height = _overlay.Height = _diagramSize.Height;

        // AC-841: the objects and how far one SVG unit was stretched to get onto the control, so a click maps back to
        // the id the source uses. The selection is looked up again — the agent may have removed what was selected.
        var svgWidth = DiagramSurfaceMap.Width(_currentSvg);
        _svgScale = svgWidth > 0 ? _diagramSize.Width / svgWidth : 1;
        _objects = DiagramSurfaceMap.Read(_currentSvg);
        if (_selected is { } held)
        {
            _selected = _objects.FirstOrDefault(o => o.HoldKey == held.HoldKey);
            if (_selected is null)
            {
                _registry?.ReleaseObject(_surfaceId, held.HoldKey);
            }
        }

        // AC-978: unconditional (not folded into _RefreshEditSupport below) — that call is skipped entirely
        // without a registry, and an object-less diagram still needs its hint on an older host.
        _UpdateEmptyState();
        _RefreshOverlay();
        _RefreshEditSupport();

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

    // The zoom/pan surface (AC-837): a plain Border, not a ScrollViewer — panning is our own RenderTransform
    // math, not scroll offset, so a huge diagram never grows the layout past the window around it.
    private Border _BuildViewport()
    {
        // AC-924: Focusable since the object menu's keyboard route (Menu key / Shift+F10) needs a focused control
        // to fire ContextRequested against — the diagram had none of the three surfaces' keyboard routes before this.
        // AC-978: `_emptyState` is a sibling of `_surface`, not a child of it, so it tracks the viewport's own
        // bounds and stays put — never scaled or panned away — same reasoning as WhiteboardCanvasControl's.
        var viewport = new Border
        {
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Focusable = true,
            Child = new Panel { Children = { _surface, _emptyState } },
        };
        viewport.SizeChanged += (_, e) =>
        {
            _emptyState.Width = e.NewSize.Width;
            _emptyState.Height = e.NewSize.Height;
            if (_isFitMode)
            {
                _ApplyFit();
            }
        };
        viewport.AddHandler(InputElement.PointerWheelChangedEvent, _OnViewportWheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        viewport.PointerPressed += _OnViewportPointerPressed;
        viewport.PointerMoved += _OnViewportPointerMoved;
        viewport.PointerReleased += _OnViewportPointerReleased;
        viewport.PointerCaptureLost += (_, _) => _EndPan();
        // Two clicks in connect mode are the two ends of a connection, not a rename.
        viewport.DoubleTapped += (_, e) =>
        {
            if (!_isConnecting)
            {
                _StartRename(_ObjectAt(e.GetPosition(_surface)));
            }
        };

        // AC-924: the object's own menu. A right-click during Connect mode aborts that mode instead (same as a
        // click off any node in _OnSurfaceClicked) and opens no menu. Otherwise: a position selects whatever is
        // under it; no position falls back to the current selection, opening nothing if that is nothing too.
        viewport.ContextRequested += (_, args) =>
        {
            if (_isConnecting)
            {
                _SetConnecting(false);
                return;
            }

            var hit = _selected;
            if (args.TryGetPosition(_surface, out var point))
            {
                hit = _ObjectAt(point);
            }

            if (hit is not { } target)
            {
                return;
            }

            if (target != _selected)
            {
                _Select(target);
            }

            viewport.ContextMenu = _BuildObjectContextMenu(target);
            viewport.ContextMenu.Open(viewport);
            args.Handled = true;
        };

        return viewport;
    }

    // AC-924: every item calls the same method as its toolbar counterpart and reads that button's own IsEnabled,
    // so the menu never drifts from the toolbar. The popup items (AC-703): posted onto the dispatcher, anchored
    // on the toolbar button, so none of them ever opens from inside this menu's own Click routing.
    private ContextMenu _BuildObjectContextMenu(DiagramObjectAt target)
    {
        var er = _support.Dialect == DiagramEditDialect.Er;
        var items = new List<Control>();

        if (target.Kind == DiagramObjectAt.Node)
        {
            items.Add(_MenuItemFor("Rename", _renameButton, (_, _) => _StartRename(_selected)));

            items.Add(er
                ? _MenuItemFor("Attributes…", _attributesButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _EditAttributes(_attributesButton)))
                : _MenuItemFor("Shape…", _shapeButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _PickNodeShape(_shapeButton))));

            // AC-924: _SetConnecting(true) nulls _connectFrom, so the source has to be filled in right after — the
            // second click then runs through the existing _OnSurfaceClicked path, same as the toolbar's Connect.
            items.Add(_MenuItemFor("Connect from here", _connectButton, (_, _) =>
            {
                _SetConnecting(true);
                _connectFrom = target.Id;
                _RefreshHandEditBar();
            }));
        }
        else if (er)
        {
            var head = target.To!;
            items.Add(_MenuItemFor("Cardinality and label…", _deleteButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _AskRelationship(target.Id, head))));
        }
        else
        {
            items.Add(_MenuItemFor("Change label", _renameButton, (_, _) => _StartRename(_selected)));
        }

        items.Add(_MenuItemFor("Delete", _deleteButton, (_, _) => _DeleteSelected()));
        items.Add(new Separator());
        items.Add(_MenuItemFor("Ask the agent…", _askButton, (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _AddAsk(_askButton, _selected))));

        return new ContextMenu { ItemsSource = items };
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

    private void _OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        _CancelFollow();
        e.Handled = true;
        _ZoomAround(e.GetPosition(_viewport), _zoom * Math.Pow(WheelZoomStepBase, e.Delta.Y));
    }

    // Only real Avalonia pointer/wheel input reaches these two handlers — _FollowTo never raises them itself — so
    // this can cancel unconditionally with no "was this self-triggered" flag (AC-621's precedent needed one because
    // it had to tell apart two different causes of the same event; there is no such ambiguity here).
    private void _CancelFollow()
    {
        _following = false;
        _followToggle.IsChecked = false;
    }

    // AC-837's input convention stands: plain left-drag pans. AC-841 adds no gesture of its own — a press that never
    // travels is a click on an object, and connecting is an explicit mode, so pan and edit are never guessed apart.
    private void _OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedOn = _ObjectAt(e.GetPosition(_surface));
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

        // Cancelled here, not on every pointer move over the viewport — hovering the mouse is not a gesture, an
        // actual drag-pan is.
        _CancelFollow();
        Vector travelled = e.GetPosition(_viewport) - _panPointerStart;
        _panOffset = _panOffsetStart + travelled;
        _isFitMode = false;
        _ApplyTransform();

        // Dragging a node is the one thing this surface will not do: Mermaid has no coordinates. AC-910's D-6: offer
        // asking the agent right there, about the object under the drag (_pressedOn, captured into a local since
        // the field clears once the drag ends), not whatever _selected happens to hold.
        if (_pressedOn is { Kind: DiagramObjectAt.Node } pressed && !_placementHintShown && travelled.Length > ClickSlopPx * 4)
        {
            _placementHintShown = true;
            _host.ShowToast(
                "A diagram places itself — free dragging happens on the whiteboard. Here you edit the structure.",
                PluginToastSeverity.Information,
                actionLabel: "Ask the agent…",
                onAction: () => _AddAsk(_askButton, pressed));
        }
    }

    private void _OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var wasPanning = _isPanning;
        Vector travelled = e.GetPosition(_viewport) - _panPointerStart;
        _EndPan();

        if (wasPanning && travelled.Length <= ClickSlopPx)
        {
            _OnSurfaceClicked(_ObjectAt(e.GetPosition(_surface)));
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

    // ---- Hand-editing on the surface itself (AC-841/D-5) ----

    private DiagramObjectAt? _ObjectAt(Point surfacePoint) => _svgScale <= 0
        ? null
        : DiagramSurfaceMap.At(_objects, new DiagramPoint(surfacePoint.X / _svgScale, surfacePoint.Y / _svgScale));

    private void _OnSurfaceClicked(DiagramObjectAt? hit)
    {
        if (!_isConnecting)
        {
            _Select(hit);
            return;
        }

        // Connecting is two clicks in an explicit mode: the tail, then the head. Anything else ends the mode rather
        // than leaving the operator in it without knowing.
        if (hit is not { Kind: DiagramObjectAt.Node } node)
        {
            _SetConnecting(false);
            return;
        }

        if (_connectFrom is null)
        {
            _connectFrom = node.Id;
            _RefreshHandEditBar();
            return;
        }

        var from = _connectFrom;
        _SetConnecting(false);
        if (_support.Dialect == DiagramEditDialect.Er)
        {
            _AskRelationship(from, node.Id);
            return;
        }

        if (_objects.FirstOrDefault(o => o.Kind == DiagramObjectAt.Node && o.Id == from) is { } fromNode)
        {
            _StartConnectLabel(fromNode, node);
            return;
        }

        _Apply(new DiagramHandEdit(DiagramHandEditKind.Connect, from, node.Id));
    }

    // AC-909: the label connect_nodes could always carry, now offered on the operator's own Connect gesture too
    // — a box over the connection's midpoint. The connection is already decided by the two clicks that got here,
    // so Escape does not cancel it; it only leaves the label empty, same as connecting without typing anything.
    private void _StartConnectLabel(DiagramObjectAt from, DiagramObjectAt to)
    {
        var mid = new Point(
            (from.Bounds.Center.X + to.Bounds.Center.X) / 2 * _svgScale,
            (from.Bounds.Center.Y + to.Bounds.Center.Y) / 2 * _svgScale);
        var box = new TextBox
        {
            PlaceholderText = "Label (optional)",
            MinWidth = 90,
            FontSize = 13,
            Padding = new Thickness(4, 2),
        };
        Canvas.SetLeft(box, mid.X - box.MinWidth / 2);
        Canvas.SetTop(box, mid.Y - 12);
        _overlay.Children.Add(box);
        box.Focus();

        void Finish(string? label)
        {
            _overlay.Children.Remove(box);
            _Apply(new DiagramHandEdit(DiagramHandEditKind.Connect, from.Id, to.Id, Label: label));
        }

        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Finish(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim());
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                Finish(null);
            }
        };
    }

    // Selecting is holding: while the operator has an object under their hand the agent's edit naming it is refused
    // (AC-852's hold), and everything else in the diagram stays open to it.
    private void _Select(DiagramObjectAt? hit)
    {
        if (_selected is { } previous)
        {
            _registry?.ReleaseObject(_surfaceId, previous.HoldKey);
        }

        _selected = hit;
        if (hit is not null)
        {
            _registry?.HoldObject(_surfaceId, hit.HoldKey);
        }

        _presence.SetOperatorWriting(_selected is not null);
        _RefreshOverlay();
        _RefreshHandEditBar();
        _RefreshCouplingBar();
    }

    // AC-848: a click on an activity-strip line jumps to the object it named. A highlight only, deliberately not
    // _Select — that would take the operator-hold an agent's edits are refused against (AC-852), which a line of
    // history has no business acquiring.
    private async Task _FlashObjectAsync(string holdKey)
    {
        var target = _Locate(holdKey);
        if (target is null)
        {
            _host.ShowToast("That object is no longer on this diagram.", PluginToastSeverity.Information);
            return;
        }

        var bounds = new Rect(
            target.Bounds.X * _svgScale,
            target.Bounds.Y * _svgScale,
            target.Bounds.Width * _svgScale,
            target.Bounds.Height * _svgScale).Inflate(4);
        var outline = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            BorderThickness = new Thickness(2),
            BorderBrush = _Brush("CockpitAccentBrush"),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);

        _overlay.Children.Clear();
        _overlay.Children.Add(outline);
        await Task.Delay(1200);
        _RefreshOverlay();
    }

    // AC-899: an ER journal key names its object plus what changed about it — "OLD>NEW" for a rename, "ENTITY.attr"
    // for an attribute — so a jump lands on the entity that is actually on the surface rather than on nothing.
    private DiagramObjectAt? _Locate(string key) =>
        _objects.FirstOrDefault(o => o.HoldKey == key)
        ?? _objects.FirstOrDefault(o => key.Split('>', '.').Contains(o.Id, StringComparer.Ordinal));

    private void _SetConnecting(bool on)
    {
        _isConnecting = on;
        _connectFrom = null;
        _RefreshHandEditBar();
    }

    // Renaming happens where the object is: a box over the node (or, for a connection, at its midpoint), Enter to
    // keep it, Escape to leave it as it was. An ER relationship's label is set through _AskRelationship instead —
    // it also carries cardinality, which a bare label box cannot ask for.
    private void _StartRename(DiagramObjectAt? hit)
    {
        if (hit is null || _registry is null)
        {
            return;
        }

        if (hit.Kind == DiagramObjectAt.Edge)
        {
            if (_support.Dialect == DiagramEditDialect.Flowchart)
            {
                _StartRelabelConnection(hit);
            }

            return;
        }

        if (hit.Kind != DiagramObjectAt.Node)
        {
            return;
        }

        var node = hit;
        _Select(node);
        var box = new TextBox
        {
            Text = node.Label,
            MinWidth = Math.Max(80, node.Bounds.Width * _svgScale),
            FontSize = 13,
            Padding = new Thickness(4, 2),
        };
        Canvas.SetLeft(box, node.Bounds.X * _svgScale);
        Canvas.SetTop(box, node.Bounds.Y * _svgScale);
        _overlay.Children.Add(box);
        box.SelectAll();
        box.Focus();

        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _Apply(_support.Dialect == DiagramEditDialect.Er
                    ? new DiagramHandEdit(DiagramHandEditKind.RenameEntity, node.Id, Label: box.Text ?? node.Id)
                    : new DiagramHandEdit(DiagramHandEditKind.RenameNode, node.Id, Label: box.Text ?? node.Label));
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _RefreshOverlay();
            }
        };
    }

    // AC-909: achteraf hernomen for an existing connection's label — same box, positioned at the connection's own
    // midpoint and pre-filled with its current label. Escape here does cancel (unlike _StartConnectLabel): the
    // connection already exists and has a label of its own to fall back on, so there is something to leave alone.
    private void _StartRelabelConnection(DiagramObjectAt edge)
    {
        _Select(edge);
        var mid = edge.Bounds.Center;
        var box = new TextBox
        {
            Text = edge.Label,
            MinWidth = 90,
            FontSize = 13,
            Padding = new Thickness(4, 2),
        };
        Canvas.SetLeft(box, (mid.X * _svgScale) - (box.MinWidth / 2));
        Canvas.SetTop(box, (mid.Y * _svgScale) - 12);
        _overlay.Children.Add(box);
        box.SelectAll();
        box.Focus();

        box.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _Apply(new DiagramHandEdit(DiagramHandEditKind.RelabelConnection, edge.Id, edge.To, Label: box.Text));
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _RefreshOverlay();
            }
        };
    }

    // A new node is named as it is made, and gets an id of its own — the label carries the wording, the id is what
    // connections are written in terms of (an ER entity has no such split, AC-899). AC-909: a flowchart node also
    // gets a shape picker beside the name field, landing as its own SetNodeShape edit right after AddNode.
    private void _AddObject(Control anchor)
    {
        var isEntity = _support.Dialect == DiagramEditDialect.Er;
        var name = new TextBox { Width = 200, PlaceholderText = isEntity ? "Entity name" : "Node name" };
        var shape = DiagramNodeShape.Rectangle;
        var shapePreview = new NodeShapePreview { Kind = shape, Width = 28, Height = 20 };
        var shapeButton = new Button { Content = shapePreview, Classes = { "Compact" }, IsVisible = !isEntity };
        ToolTip.SetTip(shapeButton, "Choose a shape.");
        shapeButton.Flyout = _BuildNodeShapeFlyout(picked =>
        {
            shape = picked;
            shapePreview.Kind = picked;
            shapePreview.InvalidateVisual();
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { name, shapeButton } };
        var confirm = new Button { Content = "Add", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { row, confirm } },
        };

        void Add()
        {
            flyout.Hide();
            var typed = name.Text?.Trim();
            if (isEntity)
            {
                _Apply(new DiagramHandEdit(DiagramHandEditKind.AddEntity, string.IsNullOrEmpty(typed) ? _NextEntityId() : typed));
                return;
            }

            var id = _NextNodeId();
            _Apply(new DiagramHandEdit(DiagramHandEditKind.AddNode, id, Label: string.IsNullOrEmpty(typed) ? "New node" : typed));
            if (shape != DiagramNodeShape.Rectangle)
            {
                _Apply(new DiagramHandEdit(DiagramHandEditKind.SetNodeShape, id) { Shape = shape });
            }
        }

        confirm.Click += (_, _) => Add();
        name.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Add();
            }
        };

        flyout.ShowAt(anchor);
        name.Focus();
    }

    // AC-899: an entity's attributes, listed as they stand with a way to take one out, and one row of inputs to put
    // one in — the same call covers adding and changing, so typing an existing name overwrites that attribute.
    private void _EditAttributes(Control anchor)
    {
        if (_registry is null || _selected is not { Kind: DiagramObjectAt.Node } entity)
        {
            return;
        }

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(12), MinWidth = 300 };
        var flyout = new Flyout { Content = body };

        void Rebuild()
        {
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = $"Attributes of {entity.Id}", FontWeight = FontWeight.SemiBold });
            foreach (var attribute in _registry.EntityAttributes(_surfaceId, entity.Id))
            {
                var remove = new Button { Content = "×", Classes = { "Compact" }, MinWidth = 24 };
                remove.Click += (_, _) =>
                {
                    _Apply(new DiagramHandEdit(DiagramHandEditKind.RemoveAttribute, entity.Id) { Attribute = attribute.Name });
                    Rebuild();
                };
                body.Children.Add(new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        remove,
                        new TextBlock
                        {
                            Text = string.Join(" ", new[] { attribute.Type, attribute.Name, attribute.Key }.Where(part => !string.IsNullOrEmpty(part))),
                            VerticalAlignment = VerticalAlignment.Center,
                            FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                            FontSize = 12,
                        },
                    },
                });
                DockPanel.SetDock(remove, Dock.Right);
            }

            var type = new TextBox { Width = 90, PlaceholderText = "type" };
            var attributeName = new TextBox { Width = 110, PlaceholderText = "name" };
            string?[] markers = [null, "PK", "FK", "UK"];
            var key = new ComboBox { ItemsSource = new[] { "—", "PK", "FK", "UK" }, SelectedIndex = 0, MinWidth = 64 };
            var add = new Button { Content = "Add", Classes = { "Compact" } };
            add.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(attributeName.Text))
                {
                    return;
                }

                _Apply(new DiagramHandEdit(DiagramHandEditKind.SetAttribute, entity.Id)
                {
                    Attribute = attributeName.Text!.Trim(),
                    AttributeType = string.IsNullOrWhiteSpace(type.Text) ? "string" : type.Text!.Trim(),
                    AttributeKey = markers[Math.Clamp(key.SelectedIndex, 0, markers.Length - 1)],
                });
                Rebuild();
            };

            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { type, attributeName, key, add },
            });
        }

        Rebuild();
        flyout.ShowAt(anchor);
    }

    // AC-899: a relationship is not drawn until the operator has said how many of each entity take part and what the
    // line reads as — Mermaid draws all three, and there is no sensible default for any of them.
    private void _AskRelationship(string from, string to)
    {
        var fromCardinality = _CardinalityBox();
        var toCardinality = _CardinalityBox();
        var label = new TextBox { Width = 200, PlaceholderText = "reads as… (required, e.g. places)" };
        var confirm = new Button { Content = "Connect", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
        ToolTip.SetTip(confirm, "Give the line something to read as first — there's no sensible default.");
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock { Text = $"{from} → {to}", FontWeight = FontWeight.SemiBold },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { Text = from, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80 }, fromCardinality } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { new TextBlock { Text = to, VerticalAlignment = VerticalAlignment.Center, MinWidth = 80 }, toCardinality } },
                    label,
                    confirm,
                },
            },
        };

        void Relate()
        {
            if (string.IsNullOrWhiteSpace(label.Text))
            {
                return;
            }

            flyout.Hide();
            _Apply(new DiagramHandEdit(DiagramHandEditKind.Relate, from, to, label.Text!.Trim())
            {
                FromCardinality = _CardinalityAt(fromCardinality.SelectedIndex),
                ToCardinality = _CardinalityAt(toCardinality.SelectedIndex),
            });
        }

        confirm.Click += (_, _) => Relate();
        label.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Relate();
            }
        };
        label.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(label.Text);

        flyout.ShowAt(_connectButton);
        label.Focus();
    }

    private static ComboBox _CardinalityBox() =>
        new() { ItemsSource = new[] { "exactly one", "zero or one", "one or more", "zero or more" }, SelectedIndex = 0, MinWidth = 130 };

    private static DiagramErCardinality _CardinalityAt(int index) => index switch
    {
        1 => DiagramErCardinality.ZeroOrOne,
        2 => DiagramErCardinality.OneOrMore,
        3 => DiagramErCardinality.ZeroOrMore,
        _ => DiagramErCardinality.One,
    };

    private static readonly (DiagramNodeShape Kind, string Label)[] NodeShapeMenuEntries =
    [
        (DiagramNodeShape.Rectangle, "Rectangle"),
        (DiagramNodeShape.Rounded, "Rounded"),
        (DiagramNodeShape.Diamond, "Diamond"),
        (DiagramNodeShape.Stadium, "Stadium"),
        (DiagramNodeShape.Subroutine, "Subroutine"),
    ];

    // AC-909: "Shape…" on a selected node — the same grid-of-previews flyout add-node uses, applied straight away
    // as its own SetNodeShape journal line rather than staged in a form.
    private void _PickNodeShape(Control anchor)
    {
        if (_registry is null || _selected is not { Kind: DiagramObjectAt.Node } node)
        {
            return;
        }

        var flyout = _BuildNodeShapeFlyout(shape => _Apply(new DiagramHandEdit(DiagramHandEditKind.SetNodeShape, node.Id) { Shape = shape }));
        if (anchor is Button button)
        {
            button.Flyout = flyout;
        }

        flyout.ShowAt(anchor);
    }

    // Mirrors WhiteboardControl._BuildShapeFlyout's own pattern: a WrapPanel of preview-plus-label buttons, no
    // Mermaid syntax anywhere on the picker (AC-909's fourth acceptance criterion).
    private static Flyout _BuildNodeShapeFlyout(Action<DiagramNodeShape> onPick)
    {
        var flyout = new Flyout();
        var grid = new WrapPanel { MaxWidth = 160 };
        foreach (var (kind, label) in NodeShapeMenuEntries)
        {
            grid.Children.Add(_NodeShapeEntryButton(flyout, kind, label, onPick));
        }

        flyout.Content = new StackPanel { Spacing = 4, Margin = new Thickness(4), Children = { grid } };
        return flyout;
    }

    private static Button _NodeShapeEntryButton(Flyout flyout, DiagramNodeShape kind, string label, Action<DiagramNodeShape> onPick)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new NodeShapePreview { Kind = kind, Width = 44, Height = 30 },
                    new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
            Classes = { "Compact" },
        };
        button.Click += (_, _) =>
        {
            onPick(kind);
            flyout.Hide();
        };
        return button;
    }

    // A miniature of the Mermaid shape itself, not a generic icon — mirrors WhiteboardControl.ShapePreview.
    private sealed class NodeShapePreview : Control
    {
        public DiagramNodeShape Kind { get; set; }

        public override void Render(DrawingContext context)
        {
            var pen = new Pen(_Brush("CockpitTextSecondaryBrush") ?? Brushes.Gray, 1.5);
            var rect = new Rect(Bounds.Size).Deflate(3);
            switch (Kind)
            {
                case DiagramNodeShape.Rounded:
                    context.DrawRectangle(null, pen, rect, 6, 6);
                    break;
                case DiagramNodeShape.Diamond:
                    context.DrawGeometry(null, pen, _Diamond(rect));
                    break;
                case DiagramNodeShape.Stadium:
                    context.DrawRectangle(null, pen, rect, (float)(rect.Height / 2), (float)(rect.Height / 2));
                    break;
                case DiagramNodeShape.Subroutine:
                    context.DrawRectangle(null, pen, rect);
                    var inset = Math.Min(4, rect.Width / 4);
                    context.DrawLine(pen, new Point(rect.Left + inset, rect.Top), new Point(rect.Left + inset, rect.Bottom));
                    context.DrawLine(pen, new Point(rect.Right - inset, rect.Top), new Point(rect.Right - inset, rect.Bottom));
                    break;
                default:
                    context.DrawRectangle(null, pen, rect);
                    break;
            }
        }

        private static StreamGeometry _Diamond(Rect rect)
        {
            var geometry = new StreamGeometry();
            using var ctx = geometry.Open();
            ctx.BeginFigure(new Point(rect.Center.X, rect.Top), isFilled: false);
            ctx.LineTo(new Point(rect.Right, rect.Center.Y));
            ctx.LineTo(new Point(rect.Center.X, rect.Bottom));
            ctx.LineTo(new Point(rect.Left, rect.Center.Y));
            ctx.EndFigure(true);
            return geometry;
        }
    }

    // E1, E2, … past whatever E-numbers the source already carries, the entity counterpart of _NextNodeId — only
    // reached when the operator confirmed the flyout without typing a name.
    private string _NextEntityId()
    {
        var used = _objects.Where(o => o.Kind == DiagramObjectAt.Node)
            .Select(o => o.Id)
            .Where(id => id.Length > 1 && id[0] == 'E' && id[1..].All(char.IsDigit))
            .Select(id => int.Parse(id[1..]))
            .DefaultIfEmpty(0)
            .Max();
        return $"E{used + 1}";
    }

    // N1, N2, … past whatever N-numbers the source already carries, so a hand-added node never collides with one the
    // agent (or an earlier hand-edit) put there.
    private string _NextNodeId()
    {
        var used = _objects.Where(o => o.Kind == DiagramObjectAt.Node)
            .Select(o => o.Id)
            .Where(id => id.Length > 1 && id[0] == 'N' && id[1..].All(char.IsDigit))
            .Select(id => int.Parse(id[1..]))
            .DefaultIfEmpty(0)
            .Max();
        return $"N{used + 1}";
    }

    private void _DeleteSelected()
    {
        if (_selected is not { } target)
        {
            return;
        }

        var er = _support.Dialect == DiagramEditDialect.Er;
        _Apply(target.To is { } head
            ? new DiagramHandEdit(er ? DiagramHandEditKind.Unrelate : DiagramHandEditKind.Disconnect, target.Id, head)
            : new DiagramHandEdit(er ? DiagramHandEditKind.RemoveEntity : DiagramHandEditKind.RemoveNode, target.Id));
    }

    // AC-910: asks the coupled session about `target` (or, with nothing selected, the diagram as a whole) — the
    // shared flyout/message/strip, this surface's own descriptor. Reachable from the toolbar button (target =
    // _selected) and from the drag toast (target = whatever was under the drag, D-6).
    private void _AddAsk(Control anchor, DiagramObjectAt? target)
    {
        if (!_sessionBinding.IsLive)
        {
            return;
        }

        var context = new AskContext("diagram", _surfaceId, _documentTitle, target?.HoldKey, target?.Label);
        AskFlyout.Show(anchor, "What should the agent do here?", question =>
        {
            _askStrip.Add(question, target?.HoldKey);
            _ = _sessionBinding.SendAsync(AskMessage.Compose(context, question));
        });
    }

    // One handling is one change towards the registry (AC-838's write path, under the same lock as the agent's), and
    // the re-render comes back through TextChanged — never a half state written here and repaired afterwards.
    private void _Apply(DiagramHandEdit edit)
    {
        if (_registry is null)
        {
            return;
        }

        if (_registry.ApplyHandEdit(_surfaceId, edit) is { } refusal)
        {
            _host.ShowToast(refusal, PluginToastSeverity.Warning);
        }
    }

    // AC-847: composed in layers, later on top, rather than clearing-and-drawing-one — the agent's cursor and the
    // operator's own "you're editing" mark can both be on screen at once, on different objects or even the same one.
    private void _RefreshOverlay()
    {
        _overlay.Children.Clear();

        // Layer 1: the agent's cursor — absent the moment there is no coupling or nothing to point at, same rule
        // as PresenceIndicators; never drawn for an object that no longer resolves (renamed away, removed).
        if (_current is not null && _agentCursorKey is { } cursorKey && _Locate(cursorKey) is { } agentTarget)
        {
            _DrawAgentCursor(agentTarget);
        }

        // Layer 2: the operator's own hold, drawn last so it wins if it ever lands on the same object.
        if (_selected is { } selected && !(selected.Bounds.Width <= 0 && selected.Bounds.Height <= 0))
        {
            _DrawOperatorMark(selected);
        }
    }

    private void _DrawAgentCursor(DiagramObjectAt target)
    {
        var bounds = new Rect(
            target.Bounds.X * _svgScale,
            target.Bounds.Y * _svgScale,
            target.Bounds.Width * _svgScale,
            target.Bounds.Height * _svgScale).Inflate(4);

        var outline = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            // Thinner than the operator's hold outline (Thickness(2) below) — the two have to read as different
            // things at a glance, not as the same mark in a different colour.
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush("CockpitAccentBrush"),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);

        var name = _sessionBinding.DisplayName ?? "agent";
        // "Vers geland" (glowing, filled) reads differently from "settled" (outline-only, muted) — the fade has to
        // be an observable change, not two pixel-identical states.
        var tag = _glowActive
            ? new Border
            {
                Background = _Brush("CockpitAccentBrush"),
                Padding = new Thickness(5, 1),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Child = new TextBlock { Text = name, FontSize = 10, Foreground = Brushes.White },
            }
            : new Border
            {
                BorderBrush = _Brush("CockpitStatusBusyBrush"),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(5, 1),
                CornerRadius = new CornerRadius(4),
                IsHitTestVisible = false,
                Opacity = 0.75,
                Child = new TextBlock { Text = name, FontSize = 10, Foreground = _Brush("CockpitStatusBusyBrush") },
            };
        Canvas.SetLeft(tag, bounds.X);
        Canvas.SetTop(tag, bounds.Y - 18);

        _overlay.Children.Add(outline);
        _overlay.Children.Add(tag);
    }

    private void _DrawOperatorMark(DiagramObjectAt selected)
    {
        var bounds = new Rect(
            selected.Bounds.X * _svgScale,
            selected.Bounds.Y * _svgScale,
            selected.Bounds.Width * _svgScale,
            selected.Bounds.Height * _svgScale).Inflate(4);

        var outline = new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            BorderThickness = new Thickness(2),
            BorderBrush = _Brush("CockpitAccentBrush"),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, bounds.X);
        Canvas.SetTop(outline, bounds.Y);

        var mark = new Border
        {
            Background = _Brush("CockpitAccentBrush"),
            Padding = new Thickness(5, 1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = "you're editing", FontSize = 10, Foreground = Brushes.White },
        };
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y - 18);

        _overlay.Children.Add(outline);
        _overlay.Children.Add(mark);
    }

    // AC-899: the surface decides which dialect's controls stand here, so the bar is refreshed with every render
    // rather than once at construction — an agent's edit_diagram can replace a flowchart with an ER diagram.
    private void _RefreshEditSupport()
    {
        _support = _registry?.EditSupport(_surfaceId) ?? new DiagramEditSupport(DiagramEditDialect.Flowchart, null);
        _RefreshHandEditBar();
    }

    private void _RefreshHandEditBar()
    {
        // Without a registry (an older host) there is nothing to write a hand-edit into, and on a diagram type with
        // no per-object grammar there is nothing to write — both say so by being off, with the reason in the tooltip.
        var er = _support.Dialect == DiagramEditDialect.Er;
        var editable = _registry is not null && _support.Dialect != DiagramEditDialect.Unsupported;
        var reason = _registry is null ? "This host doesn't know any diagram edits yet." : _support.Reason;

        // AC-909: a flowchart's edge can be relabeled the same way a node is renamed; an ER relationship cannot —
        // its label sits together with cardinality, which only _AskRelationship's flyout asks for.
        var relabelableEdge = _selected is { Kind: DiagramObjectAt.Edge } && !er;

        _addButton.Content = er ? "+ Entity" : "+ Node";
        _addButton.IsEnabled = editable;
        _connectButton.IsEnabled = editable;
        _renameButton.IsEnabled = editable && (_selected is { Kind: DiagramObjectAt.Node } || relabelableEdge);
        _deleteButton.IsEnabled = editable && _selected is not null;
        _attributesButton.IsVisible = er;
        _attributesButton.IsEnabled = editable && _selected is { Kind: DiagramObjectAt.Node };
        _shapeButton.IsVisible = !er;
        _shapeButton.IsEnabled = editable && _selected is { Kind: DiagramObjectAt.Node };
        _connectButton.Content = _isConnecting ? "Connecting…" : "Connect";

        var box = er ? "entity" : "node";
        ToolTip.SetTip(_addButton, reason ?? $"Place a {box} on this diagram.");
        ToolTip.SetTip(_connectButton, reason ?? $"Then click two {box}s to connect them.");
        ToolTip.SetTip(_renameButton, reason ?? (_selected switch
        {
            { Kind: DiagramObjectAt.Node } => $"Rename the selected {box}.",
            { Kind: DiagramObjectAt.Edge } when relabelableEdge => "Change the label of the selected connection.",
            _ => $"Select a {box} first to rename it.",
        }));
        ToolTip.SetTip(_deleteButton, reason ?? (_selected is null ? "Select what you want to delete first." : "Delete the selected object."));
        ToolTip.SetTip(_attributesButton, reason ?? (_selected is { Kind: DiagramObjectAt.Node } ? "Manage this entity's attributes." : "Select an entity first."));
        ToolTip.SetTip(_shapeButton, reason ?? (_selected is { Kind: DiagramObjectAt.Node } ? "Change the shape of the selected node." : "Select a node first to change its shape."));

        // AC-910: asking works on the selection or on the diagram as a whole (criterion 7), so the only real gate is
        // a live coupled session — named by the coupling bar's own button so "why can't I" points somewhere.
        _askButton.IsEnabled = _sessionBinding.IsLive;
        ToolTip.SetTip(
            _askButton,
            _sessionBinding.IsLive ? "Ask the agent about the selected object, or the whole diagram."
            : "Couple a conversation first (\"Couple…\" above) to be able to ask the agent.");

        _handHint.Text = _isConnecting
            ? _connectFrom is null ? $"Click the {box} where the connection starts." : $"Click the {box} that {_connectFrom} points to."
            : _selected switch
            {
                { Kind: DiagramObjectAt.Node } node => $"{char.ToUpperInvariant(box[0])}{box[1..]} {node.Id} selected — double-click to rename.",
                { To: { } head } edge => relabelableEdge
                    ? $"Connection {edge.Id} → {head} selected — double-click to change the label."
                    : $"Connection {edge.Id} → {head} selected.",
                _ => "",
            };
        // AC-973: the label trims with an ellipsis at MaxWidth — the tooltip carries the untrimmed text.
        ToolTip.SetTip(_handHint, _handHint.Text);
        // AC-981: the separator only makes sense between two texts — hide it when the hint is empty.
        _hintSeparator.IsVisible = _handHint.Text.Length > 0;

        // AC-978: same gate as the ask button just above — the hint only offers "Ask the agent…" when that
        // button would actually do something.
        _UpdateEmptyState();
    }

    private void _ZoomByButton(double factor) =>
        _ZoomAround(new Point(_viewport.Bounds.Width / 2, _viewport.Bounds.Height / 2), _zoom * factor);

    private void _ZoomAround(Point anchor, double requestedZoom)
    {
        (_zoom, _panOffset) = DiagramZoomMath.ZoomAround(anchor, _panOffset, _zoom, requestedZoom, MinZoom, MaxZoom);
        _isFitMode = false;
        _ApplyTransform();
    }

    // "Passend maken": recomputed from the viewport's own SizeChanged (first layout, then every resize), not from
    // a user gesture — that is what makes the first render land at true size and keeps it filling the window.
    private void _ApplyFit()
    {
        _isFitMode = true;

        // AC-978: an object-less flowchart's rendered SVG is close to zero-sized, so fitting the viewport to it
        // clamps to MaxZoom — a meaningless 800% over a blank canvas. There is nothing to fit to, so open at 100%.
        var fitZoom = _objects.Count == 0
            ? 1.0
            : DiagramZoomMath.FitZoom(_viewport.Bounds.Size, _diagramSize, MinZoom, MaxZoom);
        if (fitZoom <= 0)
        {
            return;
        }

        _zoom = fitZoom;
        _panOffset = DiagramZoomMath.CenteredPanOffset(_viewport.Bounds.Size, _diagramSize, _zoom);
        _ApplyTransform();
    }

    private void _ApplyTransform()
    {
        _surface.RenderTransform = new MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _panOffset.X, _panOffset.Y));
        _zoomLabel.Text = $"{_zoom * 100:0}%";
    }

    // AC-978: shown until the first object lands, same rule as the whiteboard's own overlay. The wording names
    // the exact toolbar buttons so the hint doubles as a map to them.
    private void _UpdateEmptyState()
    {
        _emptyState.IsVisible = _objects.Count == 0;
        _emptyState.Message = _EmptyStateMessage(_sessionBinding.IsLive);
    }

    private static string _EmptyStateMessage(bool sessionIsLive) => sessionIsLive
        ? "Empty diagram. Use + Node, Insert template…, or Ask the agent… to get started."
        : "Empty diagram. Use + Node or Insert template… to get started.";

    // AC-824: the Mermaid source is one click away — collapsed under the render, never only in memory.
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

    // AC-813: PNG and SVG only — no PDF (host-dependency decision, see AC-813), no JPG (lossy artifacts on
    // line art). Exports whatever is currently rendered, via the same StorageProvider save-picker pattern as
    // the dashboard/flow export elsewhere in the host (SessionDialogService, WorkflowManagerControl).
    private (Border Toolbar, TextBlock ZoomLabel, Button Save, TextBlock SaveStatus, Button Add,
        Button Connect, Button Rename, Button Delete, Button Attributes, Button Shape, Button Ask, TextBlock Hint,
        ToggleButton Follow, TextBlock HintSeparator) _BuildToolbar()
    {
        var export = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new MaterialIcon { Kind = MaterialIconKind.TrayArrowDown, Width = 14, Height = 14 },
                    new TextBlock { Text = "Export" },
                },
            },
            Classes = { "Compact" },
        };
        export.Click += (_, _) => new MenuFlyout
        {
            Items =
            {
                _ExportMenuItem("Export as SVG…", () => _ = _ExportSvgAsync()),
                _ExportMenuItem("Export as PNG…", () => _ShowPngOptions(export)),
            },
        }.ShowAt(export);

        // AC-837: zoom in/out + Fit, with the current level always on screen — the DoD's "zichtbaar zoomniveau".
        var zoomOut = new Button { Content = "−", Classes = { "Compact" }, MinWidth = 28 };
        zoomOut.Click += (_, _) => _ZoomByButton(1 / ButtonZoomStep);
        var zoomLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40, TextAlignment = TextAlignment.Center, FontSize = 12 };
        var zoomIn = new Button { Content = "+", Classes = { "Compact" }, MinWidth = 28 };
        zoomIn.Click += (_, _) => _ZoomByButton(ButtonZoomStep);
        var fit = new Button { Content = "Fit", Classes = { "Compact" } };
        fit.Click += (_, _) => _ApplyFit();

        // AC-847: pans (never zooms) to whatever the agent just touched, as long as this stays checked — and it is
        // unchecked itself the moment the operator pans or zooms by hand (_CancelFollow).
        var follow = new ToggleButton { Content = "Follow", Classes = { "Compact" } };
        ToolTip.SetTip(follow, "Follow the agent to whatever it's currently editing.");
        follow.IsCheckedChanged += (_, _) => _following = follow.IsChecked == true;

        // AC-840: empty is a starting point, not a dead end — a template is reachable as an explicit insert
        // rather than a silent default. AC-841 adds the rest of the hand-editing beside it: what the operator
        // clicked on the render decides what these act on.
        var insertTemplate = new Button { Content = "Insert template…", Classes = { "Compact" } };
        insertTemplate.Click += (_, _) => _ = _InsertTemplateAsync();
        // Without a registry (an older host) there is nothing to write a hand-edit into, so the buttons say so by
        // being off rather than failing silently when pressed.
        var addNode = new Button { Content = "+ Node", Classes = { "Compact" }, IsEnabled = _registry is not null };
        addNode.Click += (_, _) => _AddObject(addNode);
        var connect = new Button { Content = "Connect", Classes = { "Compact" } };
        connect.Click += (_, _) => _SetConnecting(!_isConnecting);
        var rename = new Button { Content = "Rename", Classes = { "Compact" } };
        rename.Click += (_, _) => _StartRename(_selected);
        var delete = new Button { Content = "Delete", Classes = { "Compact" } };
        delete.Click += (_, _) => _DeleteSelected();
        // AC-899: an ER entity carries its own attributes, which no flowchart node has — so this one control is
        // shown for that dialect only rather than standing there meaningless on a flowchart.
        var attributes = new Button { Content = "Attributes…", Classes = { "Compact" }, IsVisible = false };
        attributes.Click += (_, _) => _EditAttributes(attributes);
        // AC-909: the shape counterpart of Rename — a flowchart node only, same reasoning as Attributes above.
        var shape = new Button { Content = "Shape…", Classes = { "Compact" }, IsVisible = false };
        shape.Click += (_, _) => _PickNodeShape(shape);
        // AC-910: the operator's free-text ask about the selection (or, with nothing selected, the diagram as a
        // whole), sent to the coupled session the moment it is submitted — see _AddAsk.
        var ask = new Button { Content = "Ask the agent…", Classes = { "Compact" } };
        ask.Click += (_, _) => _AddAsk(ask, _selected);
        var hint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        // AC-839: where this diagram lives, next to the button that puts it there — "No file yet" is a state
        // the window shows just as well as a path.
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
        // AC-981: saveStatus (where this file lives) and hint (what to do now) are two different kinds of
        // information — without a mark between them they read as one nonsense sentence.
        var hintSeparator = new TextBlock
        {
            Text = "·",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        // AC-973: a WrapPanel of individual controls, not two DockPanel-docked StackPanels — a group that no
        // longer fits on one line wraps onto the next instead of being clipped or painted over. Export leads so it
        // is never the one pushed off, matching the criterion this ticket exists for.
        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 4,
            LineSpacing = 4,
            Children =
            {
                export, zoomOut, zoomLabel, zoomIn, fit, follow,
                insertTemplate, addNode, connect, rename, delete, attributes, shape, ask, save, saveStatus, hintSeparator, hint,
            },
        };

        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel, save, saveStatus, addNode, connect, rename, delete, attributes, shape, ask, hint, follow, hintSeparator);
    }

    // One save path for both origins (AC-839): a hand-edit and an accepted agent proposal both arrive through
    // _RenderInto, so "unsaved changes" is the same comparison for either.
    private async Task _SaveAsync()
    {
        if (_filePath is { } existing)
        {
            _Persist(text =>
            {
                DiagramCatalog.Write(existing, _documentTitle, text, _fileAsLastSeen);
                return existing;
            });
            return;
        }

        var homes = DiagramCatalog.WritableHomes(await _host.GetProjectMemoryRowsAsync(_sessionBinding.LivePaneId));
        if (homes.Count == 0)
        {
            _host.ShowToast(
                "This project has no memory path — add one in the project editor before saving a diagram.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(text => DiagramCatalog.Create(homes[0].Reference, _documentTitle, text));
            return;
        }

        // More than one memory path: ask, don't pick (AC-812). The answer stays with this diagram — it changes
        // nothing about the project settings.
        var flyout = new MenuFlyout();
        foreach (var home in homes)
        {
            var item = new MenuItem { Header = home.Label ?? home.Reference };
            item.Click += (_, _) => _Persist(text => DiagramCatalog.Create(home.Reference, _documentTitle, text));
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
            _host.ShowToast($"Saving failed: {exception.Message}", PluginToastSeverity.Error);
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
        ToolTip.SetTip(_saveStatus, _saveStatus.Text);
        _saveButton.IsEnabled = dirty || _filePath is null;
    }

    private static MenuItem _ExportMenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    // Scale and transparency are asked up front (DoD): "1x/2x/4x" over the diagram's native SVG size, and
    // transparent by default since RenderOptions.Transparent already defaults on for this pipeline.
    private void _ShowPngOptions(Control anchor)
    {
        var scale = new ComboBox { ItemsSource = new[] { "1x", "2x", "4x" }, SelectedIndex = 0, MinWidth = 70 };
        var transparent = new CheckBox { Content = "Transparent background", IsChecked = true };
        var confirm = new Button { Content = "Export…", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right };

        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(12),
                Children = { new TextBlock { Text = "Scale" }, scale, transparent, confirm },
            },
        };

        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            var factor = scale.SelectedIndex switch { 1 => 2f, 2 => 4f, _ => 1f };
            _ = _ExportPngAsync(factor, transparent.IsChecked == true);
        };

        flyout.ShowAt(anchor);
    }

    private static readonly FilePickerFileType _SvgFileType = new("SVG image") { Patterns = ["*.svg"] };
    private static readonly FilePickerFileType _PngFileType = new("PNG image") { Patterns = ["*.png"] };

    private async Task _ExportSvgAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export diagram as SVG",
            SuggestedFileName = "diagram.svg",
            DefaultExtension = "svg",
            FileTypeChoices = [_SvgFileType],
        });

        if (file is null)
        {
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(_currentSvg);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not export the diagram: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _ExportPngAsync(float scale, bool transparent)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export diagram as PNG",
            SuggestedFileName = "diagram.png",
            DefaultExtension = "png",
            FileTypeChoices = [_PngFileType],
        });

        if (file is null)
        {
            return;
        }

        if (DiagramExport.RasterizePng(_currentSvg, scale, transparent) is not { } png)
        {
            _host.ShowToast("Could not render the diagram to PNG.", PluginToastSeverity.Error);
            return;
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Could not export the diagram: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    // The "agent connected" bar (AC-810), same shape as the terminal pane's (TtyView.axaml, AC-34), now always on
    // screen (AC-834): "no agent on this diagram" is a state the window is genuinely in — after the session ended,
    // or after Disconnect — and a bar that hides itself leaves the operator no way back to a coupled one.
    private (Border Bar, TextBlock Label, TextBlock ReadChip, TextBlock EditChip, Button Couple, Button Disconnect) _BuildCouplingBar()
    {
        var parts = CouplingBarFactory.Build(_documentTitle, extraActions: []);
        // AC-810's pip is a fixed accent colour here — unlike the whiteboard's, this surface never dims it.
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
                : "No agent linked.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _sessionBinding.DisplayName ?? coupling.SessionId;

        // AC-841: both working on the same diagram at once — the moment the operator holds something while the
        // agent may edit, the bar says so too, rather than only naming who is coupled.
        _couplingLabel.Text = (coupling.HasAnyCapability, coupling.CanEdit && _selected is not null) switch
        {
            (_, true) => $"2 working at once — you and session {name}",
            (true, _) => $"Agent connected — session {name}",
            _ => $"Agent connected — session {name} (no capabilities granted yet)",
        };
        _couplingLabel.Foreground = _Brush("CockpitAccentBrush");
        SurfaceChrome.SetChip(_readChip, "read_diagram", coupling.CanRead);
        SurfaceChrome.SetChip(_editChip, "edit_diagram", coupling.CanEdit);
    }

    // The diff gate (AC-825): a proposal sits here, block by block, until the operator resolves it — Apply
    // writes only the accepted blocks' new lines, everything else keeps what was already on the surface.
    private static Border _BuildProposalPanel() => new()
    {
        Margin = new Thickness(0, 0, 0, 6),
        Padding = new Thickness(8),
        Background = _Brush("CockpitSecondaryBgBrush"),
        BorderBrush = _Brush("CockpitAccentBrush"),
        BorderThickness = new Thickness(1),
        IsVisible = false,
    };

    private void _RefreshProposalPanel()
    {
        _proposalPanel.IsVisible = _pendingProposal is not null;
        if (_pendingProposal is not { } proposal)
        {
            _proposalPanel.Child = null;
            return;
        }

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new TextBlock
        {
            Text = $"Proposal from agent — {proposal.ChangeSummary}",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitAccentBrush"),
        });

        // AC-808's fidelity report, on the proposal itself — before acceptance, not only on the result afterwards.
        if (proposal.FidelityFindings.Count > 0)
        {
            var fidelity = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
            fidelity.Children.Add(new TextBlock { Text = "The renderer dropped this:", FontSize = 11, FontWeight = FontWeight.SemiBold });
            foreach (var finding in proposal.FidelityFindings)
            {
                fidelity.Children.Add(new TextBlock { Text = $"⚠ {finding}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Goldenrod });
            }

            body.Children.Add(fidelity);
        }

        for (var index = 0; index < proposal.Blocks.Count; index++)
        {
            var block = proposal.Blocks[index];
            if (!block.IsChange)
            {
                if (block.ContextLines.Count > 1)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = $"⋯ {block.ContextLines.Count} unchanged lines ⋯",
                        FontSize = 10,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    });
                }

                continue;
            }

            body.Children.Add(_BuildChangeBlock(index, block));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var apply = new Button { Content = "Apply", Classes = { "Compact" } };
        apply.Click += (_, _) => _registry?.ResolveProposal(_surfaceId, _acceptedBlocks);
        var discard = new Button { Content = "Reject all", Classes = { "Compact" } };
        discard.Click += (_, _) => _registry?.DiscardProposal(_surfaceId);
        actions.Children.Add(apply);
        actions.Children.Add(discard);
        body.Children.Add(actions);

        _proposalPanel.Child = new ScrollViewer { MaxHeight = 260, Content = body };
    }

    private Border _BuildChangeBlock(int index, DiagramDiffBlock block)
    {
        var lines = new StackPanel { Spacing = 1 };
        foreach (var line in block.OldLines)
        {
            lines.Children.Add(new TextBlock { Text = $"− {line.Text}", FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 11, Foreground = Brushes.IndianRed });
        }

        foreach (var line in block.NewLines)
        {
            lines.Children.Add(new TextBlock { Text = $"+ {line.Text}", FontFamily = new FontFamily("Consolas,Menlo,monospace"), FontSize = 11, Foreground = Brushes.MediumSeaGreen });
        }

        var accepted = _acceptedBlocks.Contains(index);
        var status = new TextBlock { Text = accepted ? "Accepted" : "Rejected (default)", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextSecondaryBrush") };
        var acceptButton = new Button { Content = "Accept", Classes = { "Compact" } };
        acceptButton.Click += (_, _) => { _acceptedBlocks.Add(index); _RefreshProposalPanel(); };
        var rejectButton = new Button { Content = "Reject", Classes = { "Compact" } };
        rejectButton.Click += (_, _) => { _acceptedBlocks.Remove(index); _RefreshProposalPanel(); };

        return new Border
        {
            BorderThickness = new Thickness(1),
            BorderBrush = _Brush(accepted ? "CockpitAccentBrush" : "CockpitHairlineBrush"),
            Padding = new Thickness(6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    lines,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { acceptButton, rejectButton, status } },
                },
            },
        };
    }

    private static IBrush? _Brush(string resourceKey) => SurfaceChrome.Brush(resourceKey);
}
