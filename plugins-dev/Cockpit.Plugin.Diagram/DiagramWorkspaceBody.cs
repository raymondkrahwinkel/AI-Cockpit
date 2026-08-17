using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Svg.Skia;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Diagrams;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Material.Icons;
using Material.Icons.Avalonia;
using Mermaider;
using MermaidRenderOptions = Mermaider.Models.RenderOptions;

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
    private readonly Button _connectButton;
    private readonly Button _renameButton;
    private readonly Button _deleteButton;
    private readonly TextBlock _handHint;
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

        // AC-841: selection, the "jij bewerkt" marking and the rename box sit on their own canvas above the render, in
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
        (var toolbar, _zoomLabel, _saveButton, _saveStatus, _connectButton, _renameButton, _deleteButton, _handHint, _followToggle) = _BuildToolbar();
        _activityStrip = new ActivityStrip(host, _surfaceId, new DiagramActivityJournal(_registry), key => _ = _FlashObjectAsync(key));
        _presence = new PresenceIndicators(host, _surfaceId, whiteboard: false);

        Content = new DockPanel
        {
            Children = { toolbar, _couplingBar, _presence, _proposalPanel, _sourceToggle, _sourceBox, _activityStrip, _viewport },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_proposalPanel, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);

        _RenderInto(document.MermaidText);

        // AC-834: the session is named by whoever opened this window, never guessed. No pane id — or one whose
        // session is gone — lands on a not-live binding, which is the "no agent on this diagram" state.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, _RefreshCouplingBar);
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

            // A plain Couple — zero capabilities. read_diagram/edit_diagram still ask their own consent (AC-810).
            if (_sessionBinding.IsLive)
            {
                _registry.Couple(_sessionBinding.PaneId, _surfaceId);
            }
        }

        // No registry (an older host) means coupling cannot be shown or offered at all, so the bar goes rather
        // than standing there with a Koppelen… button that could do nothing.
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

    // AC-847's Volgen: pan (never zoom) so the agent's just-edited object lands in the viewport's own centre, using
    // whatever zoom level is already set. Cancelled the moment the operator pans or zooms by hand — see
    // _OnViewportWheel/_OnViewportPointerMoved.
    private void _FollowTo(string objectKey)
    {
        var target = _objects.FirstOrDefault(o => o.HoldKey == objectKey);
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

    // AC-840: the AC-809 sample as an explicit insert — replaces whatever is on the surface now, same as an
    // agent's edit_diagram would, so it goes through UpdateText and reaches any coupled agent as well.
    private void _InsertSample()
    {
        _RenderInto(DiagramDocument.Sample);
        _registry?.UpdateText(_surfaceId, DiagramDocument.Sample);
    }

    private void _RenderInto(string source)
    {
        // Straight from Mermaider, no CssFlattener step: measured (AC-809) that Svg.Controls.Skia.Avalonia's own
        // CSS engine already resolves the var()/color-mix() this emits, and that CssFlattener's output renders
        // worse, not better — a separately tracked regression (AC-819), not this ticket's concern.
        var markup = MermaidRenderer.RenderSvg(source, new MermaidRenderOptions
        {
            Bg = "#1b1f27", Fg = "#e7e9ee", Line = "#3a4050", Accent = "#5b8def",
            Muted = "#9aa2b1", Surface = "#232838", Border = "#3a4050", Font = "Inter", FontSize = "13px",
        });
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

        _RefreshOverlay();
        _RefreshHandEditBar();

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
        return viewport;
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

        // Dragging a node is the one thing this surface will not do: Mermaid has no coordinates, so the next render
        // would put it back. Say where that does live rather than letting the gesture look broken.
        if (_pressedOn is { Kind: DiagramObjectAt.Node } && !_placementHintShown && travelled.Length > ClickSlopPx * 4)
        {
            _placementHintShown = true;
            _host.ShowToast(
                "Een diagram plaatst zichzelf — vrij slepen doe je op het whiteboard. Hier bewerk je de structuur.",
                PluginToastSeverity.Information);
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
        _Apply(new DiagramHandEdit(DiagramHandEditKind.Connect, from, node.Id));
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
        var target = _objects.FirstOrDefault(o => o.HoldKey == holdKey);
        if (target is null)
        {
            _host.ShowToast("Dat object staat niet meer op dit diagram.", PluginToastSeverity.Information);
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

    private void _SetConnecting(bool on)
    {
        _isConnecting = on;
        _connectFrom = null;
        _RefreshHandEditBar();
    }

    // Renaming happens where the node is: a box over the node itself, Enter to keep it, Escape to leave it as it was.
    private void _StartRename(DiagramObjectAt? hit)
    {
        if (hit is not { Kind: DiagramObjectAt.Node } node || _registry is null)
        {
            return;
        }

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
                _Apply(new DiagramHandEdit(DiagramHandEditKind.RenameNode, node.Id, Label: box.Text ?? node.Label));
            }
            else if (key.Key == Key.Escape)
            {
                key.Handled = true;
                _overlay.Children.Remove(box);
                _RefreshOverlay();
            }
        };
    }

    // A new node is named as it is made, and gets an id of its own: the label carries the wording, the id is what the
    // connections are written in terms of.
    private void _AddNode(Control anchor)
    {
        var name = new TextBox { Width = 200, PlaceholderText = "Naam van de node" };
        var confirm = new Button { Content = "Toevoegen", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { name, confirm } },
        };

        void Add()
        {
            flyout.Hide();
            var label = string.IsNullOrWhiteSpace(name.Text) ? "Nieuwe node" : name.Text!.Trim();
            _Apply(new DiagramHandEdit(DiagramHandEditKind.AddNode, _NextNodeId(), Label: label));
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

        _Apply(target.To is { } head
            ? new DiagramHandEdit(DiagramHandEditKind.Disconnect, target.Id, head)
            : new DiagramHandEdit(DiagramHandEditKind.RemoveNode, target.Id));
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
    // operator's own "jij bewerkt" mark can both be on screen at once, on different objects or even the same one.
    private void _RefreshOverlay()
    {
        _overlay.Children.Clear();

        // Layer 1: the agent's cursor — absent the moment there is no coupling or nothing to point at, same rule
        // as PresenceIndicators; never drawn for an object that no longer resolves (renamed away, removed).
        if (_current is not null && _agentCursorKey is { } cursorKey
            && _objects.FirstOrDefault(o => o.HoldKey == cursorKey) is { } agentTarget)
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
            Child = new TextBlock { Text = "jij bewerkt", FontSize = 10, Foreground = Brushes.White },
        };
        Canvas.SetLeft(mark, bounds.X);
        Canvas.SetTop(mark, bounds.Y - 18);

        _overlay.Children.Add(outline);
        _overlay.Children.Add(mark);
    }

    private void _RefreshHandEditBar()
    {
        var editable = _registry is not null;
        _connectButton.IsEnabled = editable;
        _renameButton.IsEnabled = editable && _selected is { Kind: DiagramObjectAt.Node };
        _deleteButton.IsEnabled = editable && _selected is not null;
        _connectButton.Content = _isConnecting ? "Verbinden…" : "Verbinden";

        _handHint.Text = _isConnecting
            ? _connectFrom is null ? "Klik de node waar de verbinding begint." : $"Klik de node waar {_connectFrom} naartoe wijst."
            : _selected switch
            {
                { Kind: DiagramObjectAt.Node } node => $"Node {node.Id} geselecteerd — dubbelklik om te hernoemen.",
                { To: { } head } edge => $"Verbinding {edge.Id} → {head} geselecteerd.",
                _ => "",
            };
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
        var fitZoom = DiagramZoomMath.FitZoom(_viewport.Bounds.Size, _diagramSize, MinZoom, MaxZoom);
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
        var toggle = new ToggleButton { Content = "Toon bron", Classes = { "Compact" }, Margin = new Thickness(8, 4) };
        toggle.IsCheckedChanged += (_, _) => box.IsVisible = toggle.IsChecked == true;
        return (toggle, box);
    }

    // AC-813: PNG and SVG only — no PDF (host-dependency decision, see AC-813), no JPG (lossy artifacts on
    // line art). Exports whatever is currently rendered, via the same StorageProvider save-picker pattern as
    // the dashboard/flow export elsewhere in the host (SessionDialogService, WorkflowManagerControl).
    private (Border Toolbar, TextBlock ZoomLabel, Button Save, TextBlock SaveStatus,
        Button Connect, Button Rename, Button Delete, TextBlock Hint, ToggleButton Follow) _BuildToolbar()
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
        var follow = new ToggleButton { Content = "Volgen", Classes = { "Compact" } };
        ToolTip.SetTip(follow, "Volg de agent naar wat die nu bewerkt.");
        follow.IsCheckedChanged += (_, _) => _following = follow.IsChecked == true;

        var zoomControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOut, zoomLabel, zoomIn, fit, follow },
        };

        // AC-840: leeg is een beginpunt, niet een doodlopende weg — the AC-809 sample is reachable as an explicit
        // insert rather than a silent default. AC-841 adds the rest of the hand-editing beside it: what the operator
        // clicked on the render decides what these act on.
        var insertSample = new Button { Content = "Voorbeeld invoegen", Classes = { "Compact" } };
        insertSample.Click += (_, _) => _InsertSample();
        // Without a registry (an older host) there is nothing to write a hand-edit into, so the buttons say so by
        // being off rather than failing silently when pressed.
        var addNode = new Button { Content = "+ Node", Classes = { "Compact" }, IsEnabled = _registry is not null };
        addNode.Click += (_, _) => _AddNode(addNode);
        var connect = new Button { Content = "Verbinden", Classes = { "Compact" } };
        connect.Click += (_, _) => _SetConnecting(!_isConnecting);
        var rename = new Button { Content = "Hernoemen", Classes = { "Compact" } };
        rename.Click += (_, _) => _StartRename(_selected);
        var delete = new Button { Content = "Verwijderen", Classes = { "Compact" } };
        delete.Click += (_, _) => _DeleteSelected();
        var hint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        // AC-839: waar dit diagram woont, naast de knop die het daar zet — "Nog geen bestand" is een toestand die
        // het venster net zo goed toont als een pad.
        var save = new Button { Content = "Opslaan", Classes = { "Compact" } };
        save.Click += (_, _) => _ = _SaveAsync();
        var saveStatus = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        var handEditControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { insertSample, addNode, connect, rename, delete, save, saveStatus, hint },
        };

        var bar = new DockPanel { Children = { export, handEditControls, zoomControls } };
        DockPanel.SetDock(export, Dock.Right);
        DockPanel.SetDock(handEditControls, Dock.Left);

        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel, save, saveStatus, connect, rename, delete, hint, follow);
    }

    // Eén opslagweg voor beide herkomsten (AC-839): een hand-bewerking en een aangenomen agent-voorstel komen
    // allebei via _RenderInto binnen, dus "onbewaarde wijzigingen" is voor allebei dezelfde vergelijking.
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
                "Dit project heeft geen geheugenpad — voeg er een toe in de projecteditor voordat je een diagram opslaat.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(text => DiagramCatalog.Create(homes[0].Reference, _documentTitle, text));
            return;
        }

        // Meer dan één geheugenpad: vragen, niet kiezen (AC-812). Het antwoord blijft bij dít diagram — het
        // verandert niets aan de projectinstellingen.
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
            _host.ShowToast($"Opslaan is niet gelukt: {exception.Message}", PluginToastSeverity.Error);
            return;
        }

        _savedText = text;
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);
        _RefreshSaveBar();
    }

    private void _RefreshSaveBar()
    {
        var dirty = (_sourceBox.Text ?? "") != _savedText;
        var where = _filePath ?? "Nog geen bestand";
        _saveStatus.Text = dirty ? $"{where} · onbewaarde wijzigingen" : where;
        ToolTip.SetTip(_saveStatus, _filePath);
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
                ? $"Sessie {ended} is afgelopen — dit venster blijft open."
                : "Geen agent gekoppeld.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _sessionBinding.DisplayName ?? coupling.SessionId;

        // AC-841: allebei tegelijk in hetzelfde diagram — zodra de operator iets vasthoudt terwijl de agent mag
        // bewerken, zegt de regel dat ook, in plaats van alleen wie er gekoppeld is.
        _couplingLabel.Text = (coupling.HasAnyCapability, coupling.CanEdit && _selected is not null) switch
        {
            (_, true) => $"2 tegelijk aan het werk — jij en sessie {name}",
            (true, _) => $"Agent connected — session {name}",
            _ => $"Agent connected — session {name} (no capabilities granted yet)",
        };
        _couplingLabel.Foreground = _Brush("CockpitAccentBrush");
        SurfaceChrome.SetChip(_readChip, "read_diagram", coupling.CanRead);
        SurfaceChrome.SetChip(_editChip, "edit_diagram", coupling.CanEdit);
    }

    // The diff-poort (AC-825): a proposal sits here, block by block, until the operator resolves it — Toepassen
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
            Text = $"Voorstel van agent — {proposal.ChangeSummary}",
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _Brush("CockpitAccentBrush"),
        });

        // AC-808's trouwrapport, on the proposal itself — before acceptance, not only on the result afterwards.
        if (proposal.FidelityFindings.Count > 0)
        {
            var fidelity = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
            fidelity.Children.Add(new TextBlock { Text = "De renderer liet dit vallen:", FontSize = 11, FontWeight = FontWeight.SemiBold });
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
                        Text = $"⋯ {block.ContextLines.Count} ongewijzigde regels ⋯",
                        FontSize = 10,
                        Foreground = _Brush("CockpitTextSecondaryBrush"),
                    });
                }

                continue;
            }

            body.Children.Add(_BuildChangeBlock(index, block));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        var apply = new Button { Content = "Toepassen", Classes = { "Compact" } };
        apply.Click += (_, _) => _registry?.ResolveProposal(_surfaceId, _acceptedBlocks);
        var discard = new Button { Content = "Alles afwijzen", Classes = { "Compact" } };
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
        var status = new TextBlock { Text = accepted ? "Aangenomen" : "Afgewezen (standaard)", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = _Brush("CockpitTextSecondaryBrush") };
        var acceptButton = new Button { Content = "Aannemen", Classes = { "Compact" } };
        acceptButton.Click += (_, _) => { _acceptedBlocks.Add(index); _RefreshProposalPanel(); };
        var rejectButton = new Button { Content = "Afwijzen", Classes = { "Compact" } };
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
