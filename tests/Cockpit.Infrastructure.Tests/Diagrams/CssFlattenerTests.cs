using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-807: Svg.Skia understands neither <c>var()</c>, <c>color-mix()</c>, nor <c>rem</c> — all three are
/// features Mermaider's theming leans on — so an unresolved use falls back to the SVG spec default (no
/// stroke, black fill). These pin the nested-call case the pilot's first regex-based attempt broke on.
/// </summary>
public class CssFlattenerTests
{
    [Fact]
    public void Flatten_ResolvesASimpleVarAgainstItsRootDeclaration()
    {
        const string svg = """<svg><style>:root{--fg:#e8eaef;}</style><rect fill="var(--fg)"/></svg>""";

        var result = CssFlattener.Flatten(svg);

        Assert.Equal("""<svg><style>:root{--fg:#e8eaef;}</style><rect fill="#e8eaef"/></svg>""", result);
    }

    [Fact]
    public void Flatten_FallsBackToVarsSecondArgument_WhenThePropertyIsUndeclared()
    {
        const string svg = """<svg><rect fill="var(--missing, #112233)"/></svg>""";

        var result = CssFlattener.Flatten(svg);

        Assert.Equal("""<svg><rect fill="#112233"/></svg>""", result);
    }

    [Fact]
    public void Flatten_ResolvesNestedVarFallbacksInsideColorMix_WithoutBreakingOnTheNestedParens()
    {
        // The exact shape the pilot's regex-based attempt broke on: a var() fallback that is itself a
        // var() call, inside a color-mix() argument.
        const string svg = """
            <svg><style>:root{--accent:#2563eb;--fg:#e8eaef;--bg:#0f1116;
            --_accent-tint:color-mix(in srgb, var(--accent, var(--fg)) 8%, var(--bg));}</style>
            <rect fill="var(--_accent-tint)"/></svg>
            """;

        var result = CssFlattener.Flatten(svg);

        Assert.DoesNotContain("var(", result, StringComparison.Ordinal);
        Assert.DoesNotContain("color-mix(", result, StringComparison.Ordinal);
        // 8% of #2563eb + 92% of #0f1116, rounded per channel.
        Assert.Contains("fill=\"#111827\"", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Flatten_ConvertsLeftoverRemToPx_AtA16pxRoot()
    {
        const string svg = """<svg><style>:root{--fs-m:1rem;}</style><text font-size="0.875rem"/></svg>""";

        var result = CssFlattener.Flatten(svg);

        Assert.DoesNotContain("rem", result, StringComparison.Ordinal);
        Assert.Contains("14px", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Flatten_LeavesLiteralHexValuesAlone()
    {
        // classDef/class/style/linkStyle arrive as literal hex — the one thing that was never broken in
        // the pilot, and the flattener must not touch it.
        const string svg = """<svg><rect fill="#ffc107" stroke="#856404"/></svg>""";

        var result = CssFlattener.Flatten(svg);

        Assert.Equal(svg, result);
    }
}
