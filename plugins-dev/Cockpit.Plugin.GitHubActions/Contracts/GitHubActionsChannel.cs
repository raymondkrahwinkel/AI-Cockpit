using System.Text.Json;

namespace Cockpit.Plugin.GitHubActions.Contracts;

// AC-1394: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class GitHubActionsChannel
{
    // Payload: GitHubActionsRunsRequest. Answers the branch's recent runs for the working directory, newest first,
    // or empty when there is no repo, no branch, no gh, or no runs.
    public const string RecentRuns = "recent-runs";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record GitHubActionsRunsRequest(string WorkingDirectory, int Limit);
