using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues.Tests;

/// <summary>
/// What a session started under a project carries about the repository that project is tracked in (AC-165). The
/// point of the contribution is that the link the operator made in the project editor reaches the tools running
/// inside the session, not just this plugin's dialogs.
/// </summary>
public class GitHubRepositorySessionResourcesTests
{
    private static (GitHubRepositorySessionResources Provider, FakeCockpitHost Host) Build(string? linkedRepository)
    {
        var host = new FakeCockpitHost();
        if (linkedRepository is not null)
        {
            host.ProjectFieldValues[GitHubRepositoryField.Key] = linkedRepository;
        }

        return (new GitHubRepositorySessionResources(host), host);
    }

    [Fact]
    public async Task GetSessionResources_AProjectLinkedToARepository_SetsGhRepo()
    {
        var (provider, _) = Build("raymondkrahwinkel/AI-Cockpit");

        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-1", "project-1"));

        Assert.Equal("raymondkrahwinkel/AI-Cockpit", contribution.EnvironmentVariables["GH_REPO"]);
    }

    [Fact]
    public async Task GetSessionResources_ASessionWithNoProject_ContributesNothing()
    {
        // A session started without a project is the cockpit's oldest case, not a broken one — and it must not pick
        // up a repository from whichever project happens to be selected elsewhere.
        var (provider, host) = Build("raymondkrahwinkel/AI-Cockpit");

        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-1", ProjectId: null));

        Assert.True(contribution.IsEmpty);
        Assert.Empty(host.ProjectFieldPanesAsked);
    }

    [Fact]
    public async Task GetSessionResources_AProjectThatNamesNoRepository_ContributesNothing()
    {
        // An operator who never linked one keeps the behaviour they had: gh goes on reading the working directory.
        var (provider, _) = Build(linkedRepository: null);

        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-1", "project-1"));

        Assert.True(contribution.IsEmpty);
    }

    [Fact]
    public async Task GetSessionResources_ABlankLink_ContributesNothing()
    {
        // GH_REPO set to whitespace is worse than unset: gh would take it as a repository name and fail every
        // command, rather than falling back to the working directory.
        var (provider, _) = Build("   ");

        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-1", "project-1"));

        Assert.True(contribution.IsEmpty);
    }

    [Fact]
    public async Task GetSessionResources_AsksAboutItsOwnPane_NotWhicheverIsSelected()
    {
        // The session being started is not the selected one — it does not exist on screen yet. Passing null here
        // would read the link of whatever pane the operator happened to be looking at.
        var (provider, host) = Build("raymondkrahwinkel/AI-Cockpit");

        await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-7", "project-1"));

        Assert.Equal(new[] { "pane-7" }, host.ProjectFieldPanesAsked);
    }

    [Fact]
    public async Task GetSessionResources_ALinkWithSurroundingWhitespace_IsTidiedBeforeItReachesTheSession()
    {
        var (provider, _) = Build("  owner/repo  ");

        var contribution = await provider.GetSessionResourcesAsync(new SessionResourceRequest("pane-1", "project-1"));

        Assert.Equal("owner/repo", contribution.EnvironmentVariables["GH_REPO"]);
    }
}
