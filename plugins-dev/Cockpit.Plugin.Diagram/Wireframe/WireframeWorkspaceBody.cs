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
    private readonly Panel _render;
    private readonly Canvas _overlay;
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
    private readonly Button _saveButton;
    private readonly TextBlock _saveStatus;
    private readonly Button _addButton;
    private readonly Button _textButton;
    private readonly Button _deleteButton;
    private readonly Button _upButton;
    private readonly Button _downButton;
    private readonly Button _moveButton;
    private readonly TextBlock _handHint;
    private double _zoom = 1.0;
    private Vector _panOffset;
    private bool _isFitMode = true;
    private bool _isPanning;
    private Point _panPointerStart;
    private Vector _panOffsetStart;
    private WireframeNode? _root;
    private string? _selectedId;
    private WireframeNode? _pressedOn;
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

        // No fixed control size beyond the design canvas below: `_viewport` positions/scales `_surface` itself via
        // RenderTransform for zoom and pan, same as DiagramWorkspaceBody's `_surface`.
        //
        // AC-875: the selection mark and the inline text box sit on their own canvas above the render, inside the same
        // transform — so zoom and pan move them with the wireframe rather than beside it. The render lives in its own
        // panel so re-rendering it leaves the overlay alone.
        _render = new Panel();
        _overlay = new Canvas();
        _surface = new Panel
        {
            Width = CanvasSize.Width,
            Height = CanvasSize.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
            Children = { _render, _overlay },
        };
        _viewport = _BuildViewport();

        (_couplingBar, _couplingLabel, _readChip, _editChip, _coupleButton, _disconnectButton) = _BuildCouplingBar();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        (var toolbar, _zoomLabel, _saveButton, _saveStatus,
            _addButton, _textButton, _deleteButton, _upButton, _downButton, _moveButton, _handHint) = _BuildToolbar();
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
        _sourceBox.Text = source;
        var parsed = WireframeParser.Parse(source);
        _root = parsed.Root;
        Control content = _root is { } root ? WireframeRenderer.Render(root) : _BuildErrorPanel(parsed.Errors);

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
    }

    // The selected component in the tree as it stands right now, or null when nothing is selected or what was
    // selected has been removed.
    private WireframeNode? _Selected =>
        _root is { } root && _selectedId is { } id ? WireframeHandEdit.Find(root, id) : null;

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
    // same shape as DiagramWorkspaceBody's viewport.
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
        viewport.DoubleTapped += (_, e) => _StartTextEdit(_NodeUnder(e.Source));
        return viewport;
    }

    private void _OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        _ZoomAround(e.GetPosition(_viewport), _zoom * Math.Pow(WheelZoomStepBase, e.Delta.Y));
    }

    // AC-837's input convention stands unchanged on this surface too: plain left-drag pans, and a press that never
    // travels is a click on a component. AC-875 adds no gesture of its own, so a drag is never guessed between
    // panning and moving a component — moving is what the arrows and "Verplaats naar…" are for.
    private void _OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_viewport).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedOn = _NodeUnder(e.Source);
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

        Vector travelled = e.GetPosition(_viewport) - _panPointerStart;
        _panOffset = _panOffsetStart + travelled;
        _isFitMode = false;
        _ApplyTransform();

        // Dragging a component somewhere is the one thing this surface will not do: the format has no coordinates, so
        // the next render would put it straight back. Say where that does live rather than letting the gesture look
        // broken — same answer, same wording shape as the diagram's.
        if (_pressedOn is not null && !_placementHintShown && travelled.Length > ClickSlopPx * 4)
        {
            _placementHintShown = true;
            _host.ShowToast(
                "Een wireframe plaatst zichzelf — vrij slepen doe je op het whiteboard. Hier verplaats je een component binnen de structuur, met de pijlen of «Verplaats naar…».",
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

    // ---- Hand-editing on the surface itself (AC-875) ----

    // The component a click landed on: the nearest control at or above the hit that carries a source node (AC-871's
    // attached property). Chrome the renderer draws without a node of its own — a tab strip, a skeleton row — so
    // resolves to the component it belongs to rather than to nothing.
    private static WireframeNode? _NodeUnder(object? source)
    {
        for (var control = source as Control; control is not null; control = control.Parent as Control)
        {
            if (WireframeSource.GetNode(control) is { } node)
            {
                return node;
            }
        }

        return null;
    }

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
    }

    // Posted rather than drawn straight away: the mark is placed from the selected control's own laid-out bounds, and
    // right after a render those are not measured yet.
    private void _RefreshOverlay() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(_DrawOverlay, Avalonia.Threading.DispatcherPriority.Loaded);

    private void _DrawOverlay()
    {
        // Only the marks are cleared; an inline text box in flight keeps its place, since a re-render underneath it
        // is exactly when the operator is still typing.
        foreach (var mark in _overlay.Children.OfType<Border>().ToList())
        {
            _overlay.Children.Remove(mark);
        }

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

    private Control? _ControlFor(WireframeNode node) => _render
        .GetVisualDescendants()
        .OfType<Control>()
        .FirstOrDefault(control => ReferenceEquals(WireframeSource.GetNode(control), node));

    // Changing the wording happens where the component is: a box over the component itself, Enter to keep it, Escape
    // to leave it as it was — the diagram's rename, one folder over.
    private void _StartTextEdit(WireframeNode? node)
    {
        if (node is null || _registry is null || _ControlFor(node) is not { } control
            || control.TranslatePoint(default, _surface) is not { } origin)
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

    // A new component is named and typed as it is made, and lands either inside the selected container or straight
    // after the selected component — the two the format allows, offered as two buttons rather than guessed from where
    // the pointer was.
    private void _AddComponent(Control anchor)
    {
        if (_Selected is not { } target || _selectedId is not { } id || _root is not { } root)
        {
            return;
        }

        var chosen = WireframeNodeKind.Label;
        var palette = BuildPalette(kind => chosen = kind);
        var text = new TextBox { Width = 220, PlaceholderText = "Tekst (mag leeg)" };
        var asChild = new Button { Content = "In deze container", Classes = { "Compact" }, IsEnabled = target.IsContainer };
        var asSibling = new Button { Content = "Hieronder", Classes = { "Compact" }, IsEnabled = target != root };
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
                : WireframeHandEdit.AddSibling(root, id, keyword, wording);
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

    private void _Reorder(int delta)
    {
        if (_selectedId is { } id && _root is { } root && WireframeHandEdit.Reorder(root, id, delta) is { } edit)
        {
            _Apply(edit);
        }
    }

    // Into another container: the ones it can go into, named and numbered, rather than a drop target to aim at.
    private void _MoveInto(Control anchor)
    {
        if (_selectedId is not { } id || _root is not { } root)
        {
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var destination in WireframeHandEdit.Destinations(root, id))
        {
            var item = new MenuItem { Header = $"{_Describe(destination)} — regel {destination.Line}" };
            var into = destination.Id!;
            item.Click += (_, _) => _Apply(WireframeComponentEdit.Move(id, into, position: null));
            flyout.Items.Add(item);
        }

        if (flyout.Items.Count == 0)
        {
            _host.ShowToast("Er is geen andere container om dit component in te zetten.", PluginToastSeverity.Information);
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
        var placement = _selectedId is { } id && _root is { } root ? WireframeHandEdit.Placement(root, id) : null;

        _addButton.IsEnabled = editable && target is not null;
        _textButton.IsEnabled = editable && target is not null;
        _deleteButton.IsEnabled = editable && target is not null && target != _root;
        _upButton.IsEnabled = editable && placement is { Index: > 0 };
        _downButton.IsEnabled = editable && placement is { } at && at.Index < at.Parent.Children.Count - 1;
        _moveButton.IsEnabled = editable && target is not null && target != _root;

        _handHint.Text = target is null
            ? "Klik een component om het te bewerken."
            : $"{_Describe(target)} op regel {target.Line} — dubbelklik om de tekst te wijzigen.";
    }

    // A component named the way the operator reads it: "input «E-mailadres»", or the bare keyword when it carries no
    // text of its own.
    private static string _Describe(WireframeNode node) =>
        string.IsNullOrEmpty(node.Text) ? WireframeHandEdit.Keyword(node.Kind) : $"{WireframeHandEdit.Keyword(node.Kind)} «{node.Text}»";

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
    // read-only, AC-875 included: the source stays the truth and is rebuilt from each handling, so an edit goes
    // through the registry's per-component path where the journal and the "jij bewerkt" hold both see it.
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

    private (Border Toolbar, TextBlock ZoomLabel, Button Save, TextBlock SaveStatus,
        Button Add, Button Text, Button Delete, Button Up, Button Down, Button Move, TextBlock Hint) _BuildToolbar()
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
        // AC-875: what the operator clicked on the surface is what these buttons work on. Moving lives here rather
        // than in a drag: the format has no coordinates, so dragging stays panning (see _OnViewport…).
        var add = new Button { Content = "+ Component…", Classes = { "Compact" } };
        add.Click += (_, _) => _AddComponent(add);
        var text = new Button { Content = "Tekst…", Classes = { "Compact" } };
        text.Click += (_, _) => _StartTextEdit(_Selected);
        var delete = new Button { Content = "Verwijderen", Classes = { "Compact" } };
        delete.Click += (_, _) => _DeleteSelected();
        var up = new Button { Content = "↑", Classes = { "Compact" }, MinWidth = 28 };
        ToolTip.SetTip(up, "Eén plek naar boven binnen dezelfde container.");
        up.Click += (_, _) => _Reorder(-1);
        var down = new Button { Content = "↓", Classes = { "Compact" }, MinWidth = 28 };
        ToolTip.SetTip(down, "Eén plek naar beneden binnen dezelfde container.");
        down.Click += (_, _) => _Reorder(1);
        var move = new Button { Content = "Verplaats naar…", Classes = { "Compact" } };
        move.Click += (_, _) => _MoveInto(move);
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
            Children = { add, text, delete, up, down, move, save, saveStatus, hint },
        };

        var bar = new DockPanel { Children = { handEditControls, zoomControls } };
        DockPanel.SetDock(handEditControls, Dock.Left);
        DockPanel.SetDock(zoomControls, Dock.Right);
        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel, save, saveStatus,
            add, text, delete, up, down, move, hint);
    }

    // Eén opslagweg (AC-839's precedent, one folder over): the source box always mirrors the surface's current
    // text — an agent's edit_wireframe and the operator's own handling (AC-875) both arrive through TextChanged — so
    // "onbewaarde wijzigingen" is the same comparison for both.
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
                "Dit project heeft geen geheugenpad — voeg er een toe in de projecteditor voordat je een wireframe opslaat.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(text => WireframeCatalog.Create(homes[0].Reference, _documentTitle, text));
            return;
        }

        // Meer dan één geheugenpad: vragen, niet kiezen (AC-812). Het antwoord blijft bij dit wireframe.
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
