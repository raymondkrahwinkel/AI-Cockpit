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

        Assert.Equal("AT-1 (1-1) in AT: Faster startup\nCold start takes 4s.\nhttps://yt.example.com/issue/AT-1", rendered);
    }

    [Fact]
    public void ABlankDescription_FallsBackToAPlaceholderPhrase_RatherThanAnEmptyLine()
    {
        var issue = Issue with { Description = null };

        var rendered = PromptTemplate.Render("{description}", issue, "https://example.com");

        Assert.Equal("(no description)", rendered);
    }

    [Fact]
    public void TheDefaultTemplate_RendersWithoutLeftoverPlaceholders()
    {
        var rendered = PromptTemplate.Render(PromptTemplate.Default, Issue, "https://yt.example.com/issue/AT-1");

        Assert.DoesNotContain("{", rendered);
        Assert.DoesNotContain("}", rendered);
        Assert.Contains("AT-1", rendered);
        Assert.Contains("Faster startup", rendered);
        Assert.Contains("https://yt.example.com/issue/AT-1", rendered);
    }
}
