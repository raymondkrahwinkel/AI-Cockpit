using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Cockpit.Plugins.Abstractions.Workspaces;
using Mermaider;
using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace Cockpit.Plugin.Diagram;

// The whole body of a Diagram workspace, drawn by this plugin (AC-809) — the host draws only the tab and the
// frame. Renders a fixed sample diagram, proving the render route survives the PluginLoadContext boundary:
// SvgSource/Svg are types from Svg.Controls.Skia.Avalonia, drawn on Avalonia.Controls.Control — the host's own
// type, or this would not sit in its visual tree at all.
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

    public DiagramWorkspaceBody(IWorkspaceContext context)
    {
        // Straight from Mermaider, no CssFlattener step: measured (AC-809) that Svg.Controls.Skia.Avalonia's
        // own CSS engine already resolves the var()/color-mix() this emits, and that CssFlattener's output
        // renders worse, not better — a separately tracked regression (AC-819), not this ticket's concern.
        var markup = MermaidRenderer.RenderSvg(SampleDiagram, new MermaidRenderOptions
        {
            Bg = "#1b1f27", Fg = "#e7e9ee", Line = "#3a4050", Accent = "#5b8def",
            Muted = "#9aa2b1", Surface = "#232838", Border = "#3a4050", Font = "Inter", FontSize = "13px",
        });

        // A fixed size, not Stretch=Fill: Avalonia.Svg.Skia.Svg's first measure pass returns a small
        // placeholder before its picture is ready, and nothing here forces a second layout pass once it is —
        // a host-side concern for whichever ticket designs the real panel ([e]), not this one.
        Content = new Avalonia.Svg.Skia.Svg(baseUri: null!)
        {
            SvgSource = SvgSource.LoadFromSvg(markup),
            Stretch = Stretch.Uniform,
            Width = 400,
            Height = 200,
            Margin = new Thickness(16),
        };
    }
}
