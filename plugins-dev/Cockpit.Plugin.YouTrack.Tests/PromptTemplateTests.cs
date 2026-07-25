using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The template both "Add to prompt" and "New session" (AC-298) render from — one call, so what a session sees
/// injected and what New session prefills its composer with are never two slightly different renderings.
/// </summary>
public class PromptTemplateTests
{
    private static readonly YouTrackIssue Issue = new("1-1", "AT-1", "Faster startup", "Cold start takes 4s.", "AT", "Backlog");

    [Fact]
    public void SubstitutesEveryPlaceholder()
    {
        var rendered = PromptTemplate.Render(
            "{idReadable} ({id}) in {project}: {summary}\n{description}\n{url}",
            Issue,
            "https://yt.example.com/issue/AT-1");

        rendered.Should().Be("AT-1 (1-1) in AT: Faster startup\nCold start takes 4s.\nhttps://yt.example.com/issue/AT-1");
    }

    [Fact]
    public void ABlankDescription_FallsBackToAPlaceholderPhrase_RatherThanAnEmptyLine()
    {
        var issue = Issue with { Description = null };

        var rendered = PromptTemplate.Render("{description}", issue, "https://example.com");

        rendered.Should().Be("(no description)");
    }

    [Fact]
    public void TheDefaultTemplate_RendersWithoutLeftoverPlaceholders()
    {
        var rendered = PromptTemplate.Render(PromptTemplate.Default, Issue, "https://yt.example.com/issue/AT-1");

        rendered.Should().NotContain("{").And.NotContain("}");
        rendered.Should().Contain("AT-1").And.Contain("Faster startup").And.Contain("https://yt.example.com/issue/AT-1");
    }
}
