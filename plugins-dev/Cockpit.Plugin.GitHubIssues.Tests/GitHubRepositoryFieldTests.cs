
namespace Cockpit.Plugin.GitHubIssues.Tests;

// The repository field this plugin puts on a cockpit project (AC-317). Its key is shared with the Pull Requests
// plugin on purpose, and it only has a list to offer in CLI mode — the single-repository mode already knows its
// one repository and shelling out to `gh` for an operator who turned it off would answer a question they
// did not ask.
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
    public void Registration_AllowsMultipleRepositories()
    {
        // AC-940: a project can link more than one GitHub repository; the row-per-identifier editor UI (AC-884's
        // host layer) comes for free once this is set.
        var registration = GitHubRepositoryField.Registration(Settings(useGitHubCli: true), new GitHubGhClient());

        Assert.True(registration.AllowsMultiple);
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

    // AC-548: the issues dialog and the session picker both resolve "which repository" through this one method —
    // the sibling of YouTrackProjectField.ResolvePreferredTagAsync's own test, on the field the picker used to
    // never ask at all.
    [Fact]
    public async Task ResolvePreferredRepository_TheSessionsOwnLinkedRepository_IsReturned()
    {
        var host = new FakeCockpitHost();
        host.ProjectFieldValues[GitHubRepositoryField.Key] = "octocat/hello-world";

        var repository = await GitHubRepositoryField.ResolvePreferredRepositoryAsync(host, "pane-1", CancellationToken.None);

        Assert.Equal("octocat/hello-world", repository);
    }

    [Fact]
    public async Task ResolvePreferredRepository_NoLink_IsNull()
    {
        var host = new FakeCockpitHost();

        var repository = await GitHubRepositoryField.ResolvePreferredRepositoryAsync(host, "pane-1", CancellationToken.None);

        Assert.Null(repository);
    }

    // AC-940: the plural sibling, for the session issue picker to scope to every linked repository.
    [Fact]
    public async Task ResolvePreferredRepositories_TheSessionsOwnLinkedRepositories_AreReturnedInOrder()
    {
        var host = new FakeCockpitHost();
        host.ProjectFieldValues[GitHubRepositoryField.Key] = "octocat/waymark-api, octocat/waymark-android";

        var repositories = await GitHubRepositoryField.ResolvePreferredRepositoriesAsync(host, "pane-1", CancellationToken.None);

        Assert.Equal(["octocat/waymark-api", "octocat/waymark-android"], repositories);
    }

    [Fact]
    public async Task ResolvePreferredRepositories_NoLink_IsEmpty()
    {
        var host = new FakeCockpitHost();

        var repositories = await GitHubRepositoryField.ResolvePreferredRepositoriesAsync(host, "pane-1", CancellationToken.None);

        Assert.Empty(repositories);
    }
}
