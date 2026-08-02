namespace Cockpit.Plugin.GitHubIssues.Tests;

// The template both "Add to prompt" and "New session" render from, via the dialog's own `_RenderPrompt` —
// one call, so what a session sees injected and what New session prefills its composer with are never two
// slightly different renderings.
public class PromptTemplateTests
{
    private static readonly GitHubIssue Issue = new(42, "Fix the login redirect", "https://github.com/octocat/hello-world/issues/42", "Cold start takes 4s.", "octocat/hello-world");

    [Fact]
    public void SubstitutesEveryPlaceholder()
    {
        var rendered = PromptTemplate.Render(
            "#{number} in {owner}/{repo}: {title}\n{body}\n{url}",
            Issue,
            "octocat",
            "hello-world");

        Assert.Equal("#42 in octocat/hello-world: Fix the login redirect\nCold start takes 4s.\nhttps://github.com/octocat/hello-world/issues/42", rendered);
    }

    [Fact]
    public void ABlankBody_FallsBackToAPlaceholderPhrase_RatherThanAnEmptyLine()
    {
        var issue = Issue with { Body = null };

        var rendered = PromptTemplate.Render("{body}", issue, "octocat", "hello-world");

        Assert.Equal("(no description)", rendered);
    }

    [Fact]
    public void TheDefaultTemplate_RendersWithoutLeftoverPlaceholders()
    {
        var rendered = PromptTemplate.Render(PromptTemplate.Default, Issue, "octocat", "hello-world");

        Assert.DoesNotContain("{", rendered);
        Assert.DoesNotContain("}", rendered);
        Assert.Contains("42", rendered);
        Assert.Contains("Fix the login redirect", rendered);
        Assert.Contains("https://github.com/octocat/hello-world/issues/42", rendered);
    }
}
