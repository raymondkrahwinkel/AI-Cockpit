using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;
using Material.Icons.Avalonia;
using Mermaider;
using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace Cockpit.Plugin.Diagram;

// The whole body of a Diagram workspace (AC-809 proved the panel survives the plugin boundary; AC-810 wires it into
// the cockpit-diagram MCP coupling as a real, live surface). Registers itself with the host's IDiagramAccessRegistry
// under its own WorkspaceId, so read_diagram/edit_diagram see this panel's actual text rather than a static demo.
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

    private readonly IDiagramAccessRegistry? _registry;
    private readonly string _surfaceId;
    private readonly Avalonia.Svg.Skia.Svg _svg;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private readonly TextBlock _editChip;

    public DiagramWorkspaceBody(IWorkspaceContext context, ICockpitHost host)
    {
        _registry = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _surfaceId = context.WorkspaceId;

        // A fixed size, not Stretch=Fill: Avalonia.Svg.Skia.Svg's first measure pass returns a small placeholder
        // before its picture is ready, and nothing here forces a second layout pass once it is — a host-side
        // concern for whichever ticket designs the real panel ([e]), not this one.
        _svg = new Avalonia.Svg.Skia.Svg(baseUri: null!) { Stretch = Stretch.Uniform, Width = 400, Height = 200, Margin = new Thickness(16) };

        (_couplingBar, _couplingLabel, _readChip, _editChip) = _BuildCouplingBar();

        Content = new DockPanel
        {
            Children = { _couplingBar, _svg },
        };
        DockPanel.SetDock(_couplingBar, Dock.Top);

        _RenderInto(SampleDiagram);

        if (_registry is not null)
        {
            _registry.SurfaceOpened(_surfaceId, "Diagram", SampleDiagram);
            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.TextChanged += _OnTextChanged;
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
        _svg.SvgSource = SvgSource.LoadFromSvg(markup);
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

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
