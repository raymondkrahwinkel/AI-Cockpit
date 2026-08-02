using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.GitHubIssues;

// The field this plugin puts on a cockpit project (AC-317): which GitHub repository it lives in, as
// `owner/repo`. Stored under a key the Pull Requests plugin registers too — a repository is a repository, and
// a project linked with one plugin installed must not need relinking when the other arrives.
internal static class GitHubRepositoryField
{
    // What the link is stored under on the project. Never change it: already-linked projects are keyed by it.
    public const string Key = "github.repository";

    // AC-317, in the one place that reads it: the repository the operator linked this project to (AC-548 —
    // the issues dialog already asked; the session picker never did, so it showed every repository instead of
    // only this one). Null when there is no session, no project, or no link.
    public static Task<string?> ResolvePreferredRepositoryAsync(ICockpitHost host, string? paneId, CancellationToken cancellationToken) =>
        host.GetProjectFieldValueAsync(Key, paneId, cancellationToken);

    public static ProjectFieldRegistration Registration(GitHubIssuesSettings settings, GitHubGhClient client) =>
        new(Key, "GitHub repository", cancellationToken => _LoadOptionsAsync(settings, client, cancellationToken))
        {
            Hint = "Which repository this project lives in. The issues dialog then opens on it instead of on every repository you have.",
            Placeholder = "owner/repo",
        };

    // The owner's repositories, from `gh repo list`. Empty without the CLI mode on: the single-repository mode
    // already knows its one repository and has no list to offer, and shelling out to `gh` for an operator who
    // chose not to use it would be answering a question they did not ask.
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
