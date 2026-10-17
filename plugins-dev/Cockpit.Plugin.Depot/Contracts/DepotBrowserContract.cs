namespace Cockpit.Plugin.Depot.Contracts;

// AC-1568: what the file browser's UI asks the backend part over the channel. The backend owns every Depot call
// (the host holds the token); the UI only ever sees these shapes.
internal static class DepotBrowserChannel
{
    public const string Projects = "browser-projects";
    public const string Tree = "browser-tree";
    public const string Read = "browser-read";
    public const string DownloadUrl = "browser-download-url";
    public const string Share = "browser-share";
    public const string Unshare = "browser-unshare";
}

// One request shape for every action; each action reads the fields it needs. `Project` is the `project` argument
// as Depot addresses it (org/slug, or a bare slug on an older Depot).
internal sealed record DepotBrowserRequest(
    string ConnectionId, string? Project = null, string? Path = null, string? Mode = null, string? ShareId = null);

internal static class DepotBrowserStatus
{
    public const string Ok = "ok";

    // Not signed in: the UI offers a Sign-in button and opens nothing by itself (AC-1517).
    public const string SignIn = "sign-in";
    public const string Error = "error";
}

internal sealed record DepotBrowserAnswer<T>(string Status, string? Message, T? Data)
{
    public static DepotBrowserAnswer<T> Success(T data) => new(DepotBrowserStatus.Ok, null, data);

    public static DepotBrowserAnswer<T> Failure(string status, string? message) => new(status, message, default);
}

internal sealed record DepotBrowserProject(string Project, string Name, string Role);

internal static class DepotBrowserKind
{
    public const string Document = "document";
    public const string Artifact = "artifact";
}

// `ShareId`/`ShareUrl` are set only on a shared artifact.
internal sealed record DepotBrowserNode(
    string Path, string Kind, long Size, DateTimeOffset? Modified, string? ContentType,
    string? ShareId = null, string? ShareUrl = null, bool IsPublic = false);

// `CanShare` is the backend's own reading of the caller's role: Editor or above.
internal sealed record DepotBrowserTree(IReadOnlyList<DepotBrowserNode> Nodes, bool CanShare);

internal sealed record DepotBrowserShare(string ShareId, string Url, bool IsPublic);
