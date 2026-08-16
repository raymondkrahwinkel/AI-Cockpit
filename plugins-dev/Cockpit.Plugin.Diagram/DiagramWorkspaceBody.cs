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
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
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

    private static readonly Cursor _PanCursor = new(StandardCursorType.Hand);
    private static readonly Cursor _PanningCursor = new(StandardCursorType.SizeAll);

    private readonly ICockpitHost _host;
    private readonly IDiagramAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly Avalonia.Svg.Skia.Svg _svg;
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
    private IPluginSessionBinding _binding;
    private string? _boundSessionName;
    private string? _endedSessionName;

    public DiagramWorkspaceBody(ICockpitHost host, DiagramDocument document, string? sessionPaneId)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _surfaceId = document.Id;

        // AC-837: no fixed size and no ScrollViewer. Avalonia.Svg.Skia.Svg's own MeasureOverride returns a small
        // placeholder before its picture is ready, so layout can no longer be trusted for sizing — `_RenderInto`
        // reads the real size straight off the loaded Skia picture instead (same as DiagramExport/SvgRasterizer),
        // and `_viewport` positions/scales it itself via RenderTransform for zoom and pan.
        _svg = new Avalonia.Svg.Skia.Svg(baseUri: null!)
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
        };
        _viewport = _BuildViewport();

        (_couplingBar, _couplingLabel, _readChip, _editChip, _coupleButton, _disconnectButton) = _BuildCouplingBar();
        _proposalPanel = _BuildProposalPanel();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        (var toolbar, _zoomLabel) = _BuildToolbar();

        Content = new DockPanel
        {
            Children = { toolbar, _couplingBar, _proposalPanel, _sourceToggle, _sourceBox, _viewport },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_proposalPanel, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);

        _RenderInto(document.MermaidText);

        // AC-834: the session is named by whoever opened this window, never guessed. No pane id — or one whose
        // session is gone — lands on DetachedSessionBinding, which is the "no agent on this diagram" state.
        _binding = _Bind(sessionPaneId);

        if (_registry is not null)
        {
            _registry.SurfaceOpened(_surfaceId, document.Title, document.MermaidText);
            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.TextChanged += _OnTextChanged;
            _registry.ProposalChanged += _OnProposalChanged;

            // A plain Couple — zero capabilities. read_diagram/edit_diagram still ask their own consent (AC-810).
            if (_binding.IsLive)
            {
                _registry.Couple(_binding.PaneId, _surfaceId);
            }
        }

        // No registry (an older host) means coupling cannot be shown or offered at all, so the bar goes rather
        // than standing there with a Koppelen… button that could do nothing.
        _couplingBar.IsVisible = _registry is not null;
        _RefreshCouplingBar();

        DetachedFromVisualTree += (_, _) =>
        {
            _binding.Dispose();
            if (_registry is null)
            {
                return;
            }

            _registry.CouplingChanged -= _OnCouplingChanged;
            _registry.TextChanged -= _OnTextChanged;
            _registry.ProposalChanged -= _OnProposalChanged;
            _registry.SurfaceClosed(_surfaceId);
        };
    }

    // The name is read here and kept, not read on demand: by the time the session ends it is gone from the
    // cockpit, and "session … has ended" with no name in it is the one moment the operator needs one.
    private IPluginSessionBinding _Bind(string? paneId)
    {
        var binding = _host.BindToSession(paneId ?? "");
        _boundSessionName = binding.SessionName ?? (binding.IsLive ? binding.PaneId : null);
        binding.Ended += _OnSessionEnded;
        return binding;
    }

    // The session behind this window ended. Nothing here closes the window, and nothing here drops the coupling
    // either — the host releases it (AC-834, CockpitViewModel's driver-side teardown) and the registry's own
    // CouplingChanged brings that back. This only supplies the name that is gone by then.
    private void _OnSessionEnded(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _endedSessionName = _boundSessionName;
        _RefreshCouplingBar();
    });

    // Couples this diagram to another running session — the way out of "window open, no agent", after the bound
    // session ended or the operator disconnected. Exclusivity is the registry's (IsCoupledByAnother): a surface a
    // different agent already holds refuses, and the operator is told rather than shown an exception.
    private void _Recouple(string paneId)
    {
        try
        {
            _registry?.Couple(paneId, _surfaceId);
        }
        catch (InvalidOperationException exception)
        {
            _host.ShowToast(exception.Message, PluginToastSeverity.Error);
            return;
        }

        _binding.Ended -= _OnSessionEnded;
        _binding.Dispose();
        _binding = _Bind(paneId);
        _endedSessionName = null;
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

        if (_isFitMode)
        {
            _ApplyFit();
        }
        else
        {
            _ApplyTransform();
        }
    }

    // The zoom/pan surface (AC-837): a plain Border, not a ScrollViewer — panning is our own RenderTransform
    // math, not scroll offset, so a huge diagram never grows the layout past the window around it.
    private Border _BuildViewport()
    {
        var viewport = new Border { Background = Brushes.Transparent, ClipToBounds = true, Child = _svg };
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

    // AC-837's input convention: plain left-drag pans — there is no edit interaction on a diagram yet, so nothing
    // competes for the gesture. D-5/AC-841's hand-editing must gate its own drag behind an explicit mode (or a
    // different button/modifier) rather than overload this one, so pan and edit are never guessed apart.
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
        _svg.RenderTransform = new MatrixTransform(new Matrix(_zoom, 0, 0, _zoom, _panOffset.X, _panOffset.Y));
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
    private (Border Toolbar, TextBlock ZoomLabel) _BuildToolbar()
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
        var zoomControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOut, zoomLabel, zoomIn, fit },
        };

        var bar = new DockPanel { Children = { export, zoomControls } };
        DockPanel.SetDock(export, Dock.Right);

        return (new Border { Padding = new Thickness(8, 4), Child = bar }, zoomLabel);
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
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = _Brush("CockpitAccentBrush") };
        var readChip = _Chip();
        var editChip = _Chip();
        var disconnect = new Button { Content = "Disconnect", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        disconnect.Click += (_, _) => _registry?.Disconnect(_surfaceId);

        var couple = new Button { Content = "Koppelen…", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        couple.Click += (_, _) => _ShowSessionPicker(couple);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { couple, disconnect } };

        var bar = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 4),
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitAccentBrush"),
            BorderThickness = new Thickness(1),
            Child = new DockPanel
            {
                Children =
                {
                    actions,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new MaterialIcon { Kind = MaterialIconKind.RobotOutline, Width = 15, Height = 15, Foreground = _Brush("CockpitAccentBrush") },
                            label,
                            readChip,
                            editChip,
                        },
                    },
                },
            },
        };
        DockPanel.SetDock(actions, Dock.Right);

        return (bar, label, readChip, editChip, couple, disconnect);
    }

    // The open sessions by name (AC-833), so recoupling names a session instead of guessing one. No running
    // session is a state worth reading, not an empty menu.
    private void _ShowSessionPicker(Control anchor)
    {
        var open = _host.Sessions.OpenSessions;
        var flyout = new MenuFlyout();
        if (open.Count == 0)
        {
            flyout.Items.Add(new MenuItem { Header = "Geen open sessies", IsEnabled = false });
        }

        foreach (var session in open)
        {
            var item = new MenuItem { Header = session.Name };
            item.Click += (_, _) => _Recouple(session.PaneId);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }

    private void _RefreshCouplingBar()
    {
        var coupled = _current is not null;
        _disconnectButton.IsVisible = coupled;
        _coupleButton.IsVisible = !coupled;
        _readChip.IsVisible = coupled;
        _editChip.IsVisible = coupled;

        if (_current is not { } coupling)
        {
            _couplingLabel.Text = _endedSessionName is { } ended
                ? $"Sessie {ended} is afgelopen — dit venster blijft open."
                : "Geen agent gekoppeld.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _binding.SessionName ?? _boundSessionName ?? coupling.SessionId;
        _couplingLabel.Text = coupling.HasAnyCapability
            ? $"Agent connected — session {name}"
            : $"Agent connected — session {name} (no capabilities granted yet)";
        _couplingLabel.Foreground = _Brush("CockpitAccentBrush");
        _SetChip(_readChip, "read_diagram", coupling.CanRead);
        _SetChip(_editChip, "edit_diagram", coupling.CanEdit);
    }

    private static TextBlock _Chip() => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(6, 1),
        FontSize = 10,
    };

    private static void _SetChip(TextBlock chip, string name, bool granted)
    {
        chip.Text = granted ? $"{name} allowed" : $"{name} not granted";
        chip.Foreground = granted ? _Brush("CockpitAccentBrush") : _Brush("CockpitTextSecondaryBrush");
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

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
