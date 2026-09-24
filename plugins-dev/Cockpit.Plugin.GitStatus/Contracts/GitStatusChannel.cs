using System.Text.Json;

namespace Cockpit.Plugin.GitStatus.Contracts;

// AC-1390: what the backend part answers the UI part over the plugin's channel. Compiled into both assemblies as
// a linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class GitStatusChannel
{
    // Payload: GitStatusRequest. Answers a GitStatusBadge.
    public const string Status = "status";

    // Payload: GitStatusRequest. Answers the checked-out branch as a string, empty on a detached HEAD.
    public const string Branch = "branch";

    // Payload: GitStatusRequest. Answers the HEAD file that governs the branch, or null outside a repository.
    public const string HeadFile = "head-file";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal sealed record GitStatusRequest(string WorkingDirectory);

// What the header badge shows. Error is set when the directory is not a repository or git could not run.
internal sealed record GitStatusBadge(string? Error, string Branch, bool IsClean, string Summary);
