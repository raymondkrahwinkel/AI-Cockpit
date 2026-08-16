using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Svg.Skia;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;
using Material.Icons.Avalonia;
using Mermaider;
using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace Cockpit.Plugin.Diagram;

// The whole body of a Diagram workspace (AC-809 proved the panel survives the plugin boundary; AC-810 wired the
// cockpit-diagram MCP coupling; AC-824 makes the panel itself the surface — an embedded live conversation, AC-810's
// coupling bar unchanged, and a diagram card beside it that keeps pace with the conversation).
internal sealed class DiagramWorkspaceBody : UserControl
{
    private const string SampleDiagram = """
        flowchart LR
            Zip[Plugin zip] -->|PluginLoadContext| Fallthrough{Falls through?}
            Fallthrough -->|Avalonia, Skia| Host[Host's own copy]
            Fallthrough -->|Mermaider| Own[Plugin's own copy]
            Host --> Panel[This panel]
            Own --> Panel
        """;

    private readonly ICockpitHost _host;
    private readonly IDiagramAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly Avalonia.Svg.Skia.Svg _svg;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private readonly TextBlock _editChip;
    private readonly Border _proposalPanel;
    private readonly ToggleButton _sourceToggle;
    private readonly TextBox _sourceBox;
    private string _currentSvg = "";
    private DiagramProposal? _pendingProposal;
    private readonly HashSet<int> _acceptedBlocks = [];

    public DiagramWorkspaceBody(IWorkspaceContext context, ICockpitHost host, DiagramQuickStart? quickStart = null)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _surfaceId = context.WorkspaceId;

        // A fixed size, not Stretch=Fill: Avalonia.Svg.Skia.Svg's first measure pass returns a small placeholder
        // before its picture is ready, and nothing here forces a second layout pass once it is — a host-side
        // concern for whichever ticket designs the real panel ([e]), not this one.
        _svg = new Avalonia.Svg.Skia.Svg(baseUri: null!) { Stretch = Stretch.Uniform, Width = 340, Height = 200, Margin = new Thickness(16) };

        (_couplingBar, _couplingLabel, _readChip, _editChip) = _BuildCouplingBar();
        _proposalPanel = _BuildProposalPanel();
        (_sourceToggle, _sourceBox) = _BuildSourceToggle();
        var toolbar = _BuildToolbar();

        // AC-824: the conversation is the surface — a live embedded session, same mechanism FanOut/Autopilot use
        // (IWorkspaceContext.EmbedSession). The host owns its lifetime and ends it when this workspace closes.
        var conversation = context.EmbedSession(new EmbeddedSessionRequest()).View;

        var diagramCard = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = new DockPanel
            {
                Children = { toolbar, _couplingBar, _proposalPanel, _sourceToggle, _sourceBox, new ScrollViewer { Content = _svg } },
            },
        };
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_proposalPanel, Dock.Top);
        DockPanel.SetDock(_sourceToggle, Dock.Bottom);
        DockPanel.SetDock(_sourceBox, Dock.Bottom);

        var layout = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,380") };
        layout.Children.Add(conversation);
        layout.Children.Add(diagramCard);
        Grid.SetColumn(diagramCard, 1);
        Content = layout;

        // AC-826: a diagram opened from the list hands its title/text through here; nothing pending (the
        // toolbar's own "Diagram Builder" launch) falls back to the sample, same as before.
        var pending = DiagramOpenHandoff.Pending;
        DiagramOpenHandoff.Pending = null;
        var initialTitle = pending?.Title ?? "Diagram";
        var initialText = pending?.MermaidText ?? SampleDiagram;

        _RenderInto(initialText);

        if (_registry is not null)
        {
            // AC-816: a quick-start's name seeds the surface's display name, and coupling a chosen session here
            // is a plain Couple — zero capabilities, same as every other coupling (see DiagramQuickStart). Falls
            // back to AC-826's list hand-off (initialTitle/initialText) when there is no quick-start.
            if (quickStart is { } request)
            {
                request.ApplyTo(_registry, _surfaceId, initialText);
            }
            else
            {
                _registry.SurfaceOpened(_surfaceId, initialTitle, initialText);
            }

            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.TextChanged += _OnTextChanged;
            _registry.ProposalChanged += _OnProposalChanged;
            _RefreshCouplingBar();
        }

        DetachedFromVisualTree += (_, _) =>
        {
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
    private Border _BuildToolbar()
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

        return new Border
        {
            Padding = new Thickness(8, 4),
            Child = new DockPanel { Children = { export } },
        };
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

    // The "agent connected" bar (AC-810), same shape as the terminal pane's (TtyView.axaml, AC-34): visible for as
    // long as this surface is coupled to any agent, even with zero capabilities granted yet — that is a real,
    // visible state (AC-816's quick-start couples before either capability is ever asked for), not a hidden one.
    private (Border Bar, TextBlock Label, TextBlock ReadChip, TextBlock EditChip) _BuildCouplingBar()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = _Brush("CockpitAccentBrush") };
        var readChip = _Chip();
        var editChip = _Chip();
        var disconnect = new Button { Content = "Disconnect", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        disconnect.Click += (_, _) => _registry?.Disconnect(_surfaceId);

        var bar = new Border
        {
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(8, 4),
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitAccentBrush"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new DockPanel
            {
                Children =
                {
                    disconnect,
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
        DockPanel.SetDock(disconnect, Dock.Right);

        return (bar, label, readChip, editChip);
    }

    private void _RefreshCouplingBar()
    {
        _couplingBar.IsVisible = _current is not null;
        if (_current is not { } coupling)
        {
            return;
        }

        _couplingLabel.Text = coupling.HasAnyCapability
            ? $"Agent connected — session {coupling.SessionId}"
            : $"Agent connected — session {coupling.SessionId} (no capabilities granted yet)";
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
