
namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// The repository field this plugin puts on a cockpit project (AC-317). Its key is shared with the Pull Requests
/// plugin on purpose, and it only has a list to offer in CLI mode — the single-repository mode already knows its
/// one repository and shelling out to <c>gh</c> for an operator who turned it off would answer a question they
/// did not ask.
/// </summary>
public class GitHubRepositoryFieldTests
{
    private static GitHubIssuesSettings Settings(bool useGitHubCli) =>
        new(new InMemoryPluginStorage()) { UseGitHubCli = useGitHubCli };

    [Fact]
    public void Key_IsTheOneAlreadyLinkedProjectsAreStoredUnder()
    {
        // Shared with the Pull Requests plugin, and the key already-linked projects are stored under: changing it
        // silently unlinks every one of them.
        Assert.Equal("github.repository", GitHubRepositoryField.Key);
    }

    [Fact]
    public void Registration_DescribesTheFieldTheEditorDraws()
    {
        var registration = GitHubRepositoryField.Registration(Settings(useGitHubCli: true), new GitHubGhClient());

        Assert.Equal(GitHubRepositoryField.Key, registration.Key);
        Assert.Equal("GitHub repository", registration.Title);
        Assert.Equal("owner/repo", registration.Placeholder);
    }

    [Fact]
    public async Task LoadOptions_WithTheCliModeOff_OffersNothingWithoutRunningGh()
    {
        // A real GitHubGhClient: if this ever shells out, the test either hangs on a login prompt or fails on a
        // machine with no gh — which is exactly the regression it is here to catch.
        var registration = GitHubRepositoryField.Registration(Settings(useGitHubCli: false), new GitHubGhClient());

        var options = await registration.LoadOptionsAsync(CancellationToken.None);

        Assert.Empty(options);
    }
}
