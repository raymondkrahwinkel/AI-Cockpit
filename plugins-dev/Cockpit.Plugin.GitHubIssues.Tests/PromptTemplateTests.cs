using FluentAssertions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The template both "Add to prompt" and "New session" render from, via the dialog's own <c>_RenderPrompt</c> —
/// one call, so what a session sees injected and what New session prefills its composer with are never two
/// slightly different renderings.
/// </summary>
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

        rendered.Should().Be("#42 in octocat/hello-world: Fix the login redirect\nCold start takes 4s.\nhttps://github.com/octocat/hello-world/issues/42");
    }

    [Fact]
    public void ABlankBody_FallsBackToAPlaceholderPhrase_RatherThanAnEmptyLine()
    {
        var issue = Issue with { Body = null };

        var rendered = PromptTemplate.Render("{body}", issue, "octocat", "hello-world");

        rendered.Should().Be("(no description)");
    }

    [Fact]
    public void TheDefaultTemplate_RendersWithoutLeftoverPlaceholders()
    {
        var rendered = PromptTemplate.Render(PromptTemplate.Default, Issue, "octocat", "hello-world");

        rendered.Should().NotContain("{").And.NotContain("}");
        rendered.Should().Contain("42").And.Contain("Fix the login redirect").And.Contain("https://github.com/octocat/hello-world/issues/42");
    }
}
