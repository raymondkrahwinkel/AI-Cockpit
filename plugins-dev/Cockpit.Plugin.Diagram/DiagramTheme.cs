using MermaidRenderOptions = Mermaider.Models.RenderOptions;

namespace Cockpit.Plugin.Diagram;

// AC-911: the seven Mermaid theme colors DiagramWorkspaceBody._RenderInto and the template previews both need,
// pulled out of _RenderInto so a theme change lands in one place instead of two that quietly drift apart.
internal static class DiagramTheme
{
    public static readonly MermaidRenderOptions Options = new()
    {
        Bg = "#1b1f27", Fg = "#e7e9ee", Line = "#3a4050", Accent = "#5b8def",
        Muted = "#9aa2b1", Surface = "#232838", Border = "#3a4050", Font = "Inter", FontSize = "13px",
    };
}
