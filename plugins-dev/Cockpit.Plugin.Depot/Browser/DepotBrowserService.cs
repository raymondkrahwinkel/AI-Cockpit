using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.Browser;

// AC-1568/AC-1637: file operations use Depot's sync API through the host's authorized handler. Shares remain on MCP
// until AC-1638. Nothing here logs: a pre-signed download URL is a credential for its few minutes.
internal sealed class DepotBrowserService(ICockpitHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections)
{
    private const int PageSize = 200;

    // Stops a server that keeps handing back a cursor; 500 pages is 100k documents.
    private const int MaxPages = 500;

    private readonly ICockpitHost _host = host;
    private readonly Func<IReadOnlyList<DepotConnectionRegistration>> _connections = connections;
    private readonly DepotMcp _mcp = new(host, connections);
    private readonly DepotUploader _uploader = new();

    public Task<DepotBrowserAnswer<IReadOnlyList<DepotBrowserProject>>> ProjectsAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync<IReadOnlyList<DepotBrowserProject>>(async () =>
        {
            var rows = await _ListProjectsAsync(request, cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(row => new DepotBrowserProject(row.Argument, row.Name, row.Role.ToDisplayString() ?? "Unknown"))];
        });

    // Documents (every page of `list`) and artifacts in one flat list, a share marked on an artifact by path from a
    // single `list_shares` for the whole project.
    public Task<DepotBrowserAnswer<DepotBrowserTree>> TreeAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = DepotMcp.Required(request.Project, "project");
            var nodes = new List<DepotBrowserNode>();
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var path = $"{_SyncRoot(project)}/files?limit={PageSize}"
                    + (cursor is null ? string.Empty : $"&after={Uri.EscapeDataString(cursor)}");
                using var listing = await _GetAsync(request.ConnectionId, path, cancellationToken).ConfigureAwait(false);
                nodes.AddRange(DepotMcp.Entries(listing.RootElement).Select(entry => _Node(entry, DepotBrowserKind.Document)));
                var next = DepotMcp.String(listing.RootElement, "nextCursor");
                if (string.IsNullOrEmpty(next) || next == cursor)
                {
                    break;
                }

                cursor = next;
            }

            using var artifacts = await _GetAsync(
                request.ConnectionId, $"{_SyncRoot(project)}/artifacts", cancellationToken).ConfigureAwait(false);
            var shares = (await _SharesAsync(request, project, cancellationToken).ConfigureAwait(false))
                .GroupBy(share => share.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            nodes.AddRange(DepotMcp.Entries(artifacts.RootElement).Select(entry =>
            {
                var node = _Node(entry, DepotBrowserKind.Artifact);
                return shares.TryGetValue(node.Path, out var share)
                    ? node with { ShareId = share.ShareId, ShareUrl = share.Url, IsPublic = share.IsPublic }
                    : node;
            }));

            return new DepotBrowserTree(nodes, (await _RoleAsync(request, project, cancellationToken).ConfigureAwait(false)).CanWrite());
        });

    public Task<DepotBrowserAnswer<string>> ReadAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var path = DepotMcp.Required(request.Path, "path");
            using var document = await _PostAsync(
                request.ConnectionId, $"{_SyncRoot(DepotMcp.Required(request.Project, "project"))}/read", new[] { path }, cancellationToken)
                .ConfigureAwait(false);
            var file = _Array(document.RootElement, "files").FirstOrDefault(entry => DepotMcp.String(entry, "path") == path);
            return file.ValueKind == JsonValueKind.Object
                ? DepotMcp.String(file, "content") ?? string.Empty
                : throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return the requested file.");
        });

    // The URL goes straight back to the UI part; it is not stored and not logged here.
    public Task<DepotBrowserAnswer<string>> DownloadUrlAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var path = DepotMcp.Required(request.Path, "path");
            using var document = await _PostAsync(
                request.ConnectionId, $"{_SyncRoot(DepotMcp.Required(request.Project, "project"))}/downloads", new { prefix = path }, cancellationToken)
                .ConfigureAwait(false);
            var file = _Array(document.RootElement, "files").FirstOrDefault(entry => DepotMcp.String(entry, "path") == path);
            return DepotMcp.String(file, "downloadUrl") is { Length: > 0 } url
                ? url
                : throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return a download URL.");
        });

    // A write, so it needs the Editor role: checked here against Depot's own role for the caller, not left to the UI.
    public Task<DepotBrowserAnswer<DepotBrowserShare>> ShareAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = DepotMcp.Required(request.Project, "project");
            var path = DepotMcp.Required(request.Path, "path");
            if (request.Mode is not ("members" or "public"))
            {
                throw new DepotFailure(DepotBrowserStatus.Error, "Choose Members only or Public.");
            }

            await _RequireEditorAsync(request, project, cancellationToken).ConfigureAwait(false);
            using var document = DepotMcp.Parse(await _CallMcpAsync(
                request,
                "share_artifact",
                new Dictionary<string, object?> { ["project"] = project, ["path"] = path, ["mode"] = request.Mode },
                cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (DepotMcp.String(root, "shareId") is { Length: > 0 } id && DepotMcp.String(root, "url") is { Length: > 0 } url)
            {
                return new DepotBrowserShare(id, url, root.TryGetProperty("isPublic", out var isPublic) ? isPublic.ValueKind == JsonValueKind.True : request.Mode == "public");
            }

            // The answer carried no link: the project's share list is where Depot says what exists.
            var found = (await _SharesAsync(request, project, cancellationToken).ConfigureAwait(false)).FirstOrDefault(share => share.Path == path)
                ?? throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return the share link.");
            return new DepotBrowserShare(found.ShareId, found.Url, found.IsPublic);
        });

    public Task<DepotBrowserAnswer<bool>> UnshareAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = DepotMcp.Required(request.Project, "project");
            var shareId = DepotMcp.Required(request.ShareId, "shareId");
            await _RequireEditorAsync(request, project, cancellationToken).ConfigureAwait(false);
            await _CallMcpAsync(
                request,
                "unshare_artifact",
                new Dictionary<string, object?> { ["project"] = project, ["shareId"] = shareId },
                cancellationToken).ConfigureAwait(false);
            return true;
        });

    // A write, so the Editor role is checked here. A text file is a document (`write`); anything else gets a ticket for the
    // caller to PUT to. share "none": the default would leave a share behind when the PUT fails.
    public Task<DepotBrowserAnswer<DepotBrowserUpload>> UploadAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = DepotMcp.Required(request.Project, "project");
            var path = DepotMcp.Required(request.Path, "path");
            await _RequireEditorAsync(request, project, "Uploading", cancellationToken).ConfigureAwait(false);
            var syncRoot = _SyncRoot(project);
            using var api = _Api(request.ConnectionId);
            string? baseChecksum = null;
            if (request.Replace && DepotUploadKinds.IsText(path))
            {
                using var current = await _PostAsync(api, $"{syncRoot}/read", new[] { path }, request.ConnectionId, cancellationToken)
                    .ConfigureAwait(false);
                _RejectTruncated(current.RootElement);
                var file = _Array(current.RootElement, "files").FirstOrDefault(entry => DepotMcp.String(entry, "path") == path);
                baseChecksum = DepotMcp.String(file, "checksum");
            }

            var started = await _uploader.StartAsync(
                api, _Connection(request.ConnectionId).Name, syncRoot, path, request.Content, request.ContentType ?? string.Empty,
                request.Size, baseChecksum, cancellationToken).ConfigureAwait(false);
            return new DepotBrowserUpload(started.UploadUrl);
        });

    private sealed record _Share(string ShareId, string Path, string Url, bool IsPublic);

    private async Task<IReadOnlyList<_Share>> _SharesAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken)
    {
        using var document = DepotMcp.Parse(await _CallMcpAsync(
            request, "list_shares", new Dictionary<string, object?> { ["project"] = project }, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("shares", out var shares) || shares.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. shares.EnumerateArray()
            .Where(share => DepotMcp.String(share, "shareId") is not null && DepotMcp.String(share, "path") is not null && DepotMcp.String(share, "url") is not null)
            .Select(share => new _Share(
                DepotMcp.String(share, "shareId")!,
                DepotMcp.String(share, "path")!,
                DepotMcp.String(share, "url")!,
                share.TryGetProperty("isPublic", out var isPublic) && isPublic.ValueKind == JsonValueKind.True))];
    }

    private Task _RequireEditorAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken) =>
        _RequireEditorAsync(request, project, "Sharing", cancellationToken);

    private Task _RequireEditorAsync(DepotBrowserRequest request, string project, string action, CancellationToken cancellationToken) =>
        _mcp.RequireEditorAsync(request.ConnectionId, project, action, cancellationToken);

    private Task<DepotProjectRole> _RoleAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken) =>
        _mcp.RoleAsync(request.ConnectionId, project, cancellationToken);

    private Task<IReadOnlyList<DepotProjectRow>> _ListProjectsAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _mcp.ListProjectsAsync(request.ConnectionId, cancellationToken);

    private Task<string> _CallMcpAsync(
        DepotBrowserRequest request, string tool, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken) =>
        _mcp.CallAsync(request.ConnectionId, tool, arguments, cancellationToken);

    private DepotApi _Api(string connectionId) => new(_host, _Connection(connectionId));

    private DepotConnectionRegistration _Connection(string connectionId) =>
        _connections().FirstOrDefault(candidate => candidate.Id == connectionId)
        ?? throw new DepotFailure(DepotBrowserStatus.Error, "That Depot connection is no longer configured.");

    private async Task<JsonDocument> _GetAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        using var api = new DepotApi(_host, connection);
        return await _DocumentAsync(api.GetAsync(path, cancellationToken), connection.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> _PostAsync<T>(
        string connectionId, string path, T value, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        using var api = new DepotApi(_host, connection);
        return await _PostAsync(api, path, value, connection.Name, cancellationToken).ConfigureAwait(false);
    }

    private static Task<JsonDocument> _PostAsync<T>(
        DepotApi api, string path, T value, string connectionId, CancellationToken cancellationToken) =>
        _DocumentAsync(api.PostJsonAsync(path, value, cancellationToken), connectionId, cancellationToken);

    private static async Task<JsonDocument> _DocumentAsync(
        Task<DepotApiResult> call, string connectionId, CancellationToken cancellationToken)
    {
        DepotApiResult result;
        try
        {
            result = await call.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot, or it took too long.");
        }

        if (result.Outcome == DepotApiOutcome.NotSignedIn)
        {
            throw new DepotFailure(DepotBrowserStatus.SignIn, $"Sign in to {connectionId}");
        }

        if (result.Outcome != DepotApiOutcome.Success)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not accept the file operation.");
        }

        try
        {
            return JsonDocument.Parse(result.Content);
        }
        catch (JsonException)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot's answer came back in an unexpected shape.");
        }
    }

    private static string _SyncRoot(string project)
    {
        var address = project.Split('/', 2);
        return address.Length == 2 && address.All(part => !string.IsNullOrWhiteSpace(part))
            ? $"sync/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}"
            : throw new DepotFailure(DepotBrowserStatus.Error, $"Depot project '{project}' has no organization-qualified address.");
    }

    private static IEnumerable<JsonElement> _Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];

    private static void _RejectTruncated(JsonElement root)
    {
        if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Depot truncated the file read; no partial result was shown.");
        }
    }

    private static Task<DepotBrowserAnswer<T>> _RunAsync<T>(Func<Task<T>> body) => DepotMcp.RunAsync(body);

    private static DepotBrowserNode _Node(JsonElement entry, string kind) => new(
        DepotMcp.String(entry, "path") ?? string.Empty,
        kind,
        entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
        DateTimeOffset.TryParse(DepotMcp.String(entry, "updatedAt"), out var modified) ? modified : null,
        DepotMcp.String(entry, "contentType"));
}
