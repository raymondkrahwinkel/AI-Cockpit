using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

/// <summary>
/// <see cref="MarkdownBlock"/> is a record, so callers may reasonably take it at its word and compare by value.
/// The compiler's version does not deliver that: it compares the three list properties with
/// <c>EqualityComparer&lt;T&gt;.Default</c>, which for a list is reference equality, so two identical parses came
/// out unequal. The transcript renderer relies on this comparison to repaint only the block a delta changed.
/// </summary>
public class MarkdownBlockEqualityTests
{
    [Fact]
    public void TwoSeparateParsesOfTheSameText_ProduceEqualBlocks()
    {
        const string Markdown = """
            # A heading

            A paragraph with **bold**, `code` and a [link](https://example.com).

            - first item
            - second item

            | a | b |
            | --- | --- |
            | 1 | 2 |

            ```csharp
            var x = 1;
            ```
            """;

        var left = MarkdownParser.Parse(Markdown);
        var right = MarkdownParser.Parse(Markdown);

        Assert.Equal(left.Count, right.Count);
        Assert.Equal(left, right);

        // Every kind the renderer switches on is covered above, so no list property is left untested by accident.
        Assert.Equal(
            new[]
            {
                MarkdownBlockKind.Heading,
                MarkdownBlockKind.Paragraph,
                MarkdownBlockKind.List,
                MarkdownBlockKind.Table,
                MarkdownBlockKind.CodeBlock,
            },
            left.Select(b => b.Kind));
    }

    [Fact]
    public void EqualBlocks_AgreeOnTheirHashCode()
    {
        var left = MarkdownParser.Parse("a paragraph with **bold**.\n").Single();
        var right = MarkdownParser.Parse("a paragraph with **bold**.\n").Single();

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void AParagraphThatGrewByOneWord_IsNotEqualToWhatItWas()
    {
        // The streaming case: the block a delta lands in must compare unequal, or the renderer keeps stale text.
        var before = MarkdownParser.Parse("the reply so far").Single();
        var after = MarkdownParser.Parse("the reply so far and").Single();

        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData("- one\n- two\n", "- one\n- three\n")]           // list items
    [InlineData("| a |\n| --- |\n| 1 |\n", "| a |\n| --- |\n| 2 |\n")] // table cells
    [InlineData("```\nvar x = 1;\n```\n", "```\nvar x = 2;\n```\n")]
    [InlineData("# Heading\n", "## Heading\n")]
    public void BlocksDifferingOnlyInsideAListProperty_AreNotEqual(string left, string right)
    {
        Assert.NotEqual(MarkdownParser.Parse(left).Single(), MarkdownParser.Parse(right).Single());
    }
}
