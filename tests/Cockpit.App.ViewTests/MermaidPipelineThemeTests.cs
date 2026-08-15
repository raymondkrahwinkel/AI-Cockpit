using Cockpit.App.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-807's "via het échte Avalonia-pad" half of the definition of done: <see cref="AppMermaidTheme"/> has
/// to actually resolve Theme.axaml's tokens through a running <c>Application</c>, not a hand-copied hex
/// literal that could drift from the real theme — this is the one place that resolution is exercised.
/// </summary>
[Collection("avalonia")]
public class MermaidPipelineThemeTests
{
    [Fact]
    public void Render_UsingTheLiveAppTheme_ProducesAFullyFlattenedSvg() => HeadlessAvalonia.Run(() =>
    {
        var theme = AppMermaidTheme.FromCurrentTheme();

        const string source = """
            flowchart TD
                subgraph Group
                    A[Start] --> B[End]
                end
            """;

        var document = MermaidRenderPipeline.Render(source, theme).Svg;

        Assert.DoesNotContain("var(", document.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("color-mix(", document.Markup, StringComparison.Ordinal);
        Assert.Contains(theme.Accent, document.Markup, StringComparison.OrdinalIgnoreCase);
    });
}
