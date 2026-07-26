using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubIssues;

/// <summary>
/// Tells a session started under a project which repository that project is tracked in (AC-165), by putting the
/// link the project editor already stores (AC-317) in the session's environment as <c>GH_REPO</c> — the variable
/// <c>gh</c> reads to decide which repository a command without an explicit <c>--repo</c> is about.
/// <para>
/// Without this the link only reached this plugin's own dialogs: an agent shelling out to <c>gh</c> inside the
/// session fell back to whatever repository its working directory happened to be, which is the same answer only
/// when the project's folder is the linked repository's clone.
/// </para>
/// <para>
/// Nothing is contributed for a session without a project, or for a project that names no repository — an operator
/// who never linked one gets the behaviour they had.
/// </para>
/// </summary>
internal sealed class GitHubRepositorySessionResources(ICockpitHost host) : ISessionResourceProvider
{
    /// <summary>What <c>gh</c> reads as "the repository this command is about" when none is given on the command line.</summary>
    private const string RepositoryVariable = "GH_REPO";

    public async Task<SessionResourceContribution> GetSessionResourcesAsync(
        SessionResourceRequest request,
        CancellationToken cancellationToken = default)
    {
        // A session with no project cannot carry a link, so this skips the host lookup rather than asking a question
        // whose answer is already known.
        if (string.IsNullOrEmpty(request.ProjectId))
        {
            return SessionResourceContribution.None;
        }

        var repository = await host.GetProjectFieldValueAsync(GitHubRepositoryField.Key, request.PaneId, cancellationToken);
        if (string.IsNullOrWhiteSpace(repository))
        {
            return SessionResourceContribution.None;
        }

        return new SessionResourceContribution
        {
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RepositoryVariable] = repository.Trim(),
            },
        };
    }
}
