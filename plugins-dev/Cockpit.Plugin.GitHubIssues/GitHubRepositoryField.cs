using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// The field this plugin puts on a cockpit project (AC-317): which GitHub repository it lives in, as
/// <c>owner/repo</c>. Stored under a key the Pull Requests plugin registers too — a repository is a repository, and
/// a project linked with one plugin installed must not need relinking when the other arrives.
/// </summary>
internal static class GitHubRepositoryField
{
    /// <summary>What the link is stored under on the project. Never change it: already-linked projects are keyed by it.</summary>
    public const string Key = "github.repository";

    public static ProjectFieldRegistration Registration(GitHubIssuesSettings settings, GitHubGhClient client) =>
        new(Key, "GitHub repository", cancellationToken => _LoadOptionsAsync(settings, client, cancellationToken))
        {
            Hint = "Which repository this project lives in. The issues dialog then opens on it instead of on every repository you have.",
            Placeholder = "owner/repo",
        };

    /// <summary>
    /// The owner's repositories, from <c>gh repo list</c>. Empty without the CLI mode on: the single-repository mode
    /// already knows its one repository and has no list to offer, and shelling out to <c>gh</c> for an operator who
    /// chose not to use it would be answering a question they did not ask.
    /// </summary>
    private static async Task<IReadOnlyList<ProjectFieldOption>> _LoadOptionsAsync(
        GitHubIssuesSettings settings,
        GitHubGhClient client,
        CancellationToken cancellationToken)
    {
        if (!settings.UseGitHubCli)
        {
            return [];
        }

        return
        [
            .. (await client.ListRepositoriesAsync(settings.GhOwner, cancellationToken))
                .Select(repository => new ProjectFieldOption(repository, repository)),
        ];
    }
}
