using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;

namespace Cockpit.Plugin.Depot.Browser;

// AC-1568: every Depot call behind the file browser, through the connection's own MCP server so the host holds the
// token. Nothing here logs: a pre-signed download URL is a credential for its few minutes.
internal sealed class DepotBrowserService(ICockpitHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections)
{
    private const int PageSize = 200;

    // Stops a server that keeps handing back a cursor; 500 pages is 100k documents.
    private const int MaxPages = 500;

    private sealed class _Failure(string status, string message) : Exception(message)
    {
        public string Status { get; } = status;
    }

    private sealed record _Row(string Argument, string Name, DepotProjectRole Role);

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
            var project = _Required(request.Project, "project");
            var nodes = new List<DepotBrowserNode>();
            string? cursor = null;
            for (var page = 0; page < MaxPages; page++)
            {
                var arguments = new Dictionary<string, object?> { ["project"] = project, ["limit"] = PageSize };
                if (cursor is not null)
                {
                    arguments["after"] = cursor;
                }

                using var listing = _Parse(await _CallAsync(request, "list", arguments, cancellationToken).ConfigureAwait(false));
                nodes.AddRange(_Entries(listing.RootElement).Select(entry => _Node(entry, DepotBrowserKind.Document)));
                var next = _String(listing.RootElement, "nextCursor");
                if (string.IsNullOrEmpty(next) || next == cursor)
                {
                    break;
                }

                cursor = next;
            }

            using var artifacts = _Parse(await _CallAsync(request, "list_artifacts", new Dictionary<string, object?> { ["project"] = project }, cancellationToken).ConfigureAwait(false));
            var shares = (await _SharesAsync(request, project, cancellationToken).ConfigureAwait(false))
                .GroupBy(share => share.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            nodes.AddRange(_Entries(artifacts.RootElement).Select(entry =>
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
            using var document = _Parse(await _CallAsync(
                request, "read", _Arguments(request), cancellationToken).ConfigureAwait(false));
            return _String(document.RootElement, "content") ?? string.Empty;
        });

    // The URL goes straight back to the UI part; it is not stored and not logged here.
    public Task<DepotBrowserAnswer<string>> DownloadUrlAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            using var document = _Parse(await _CallAsync(
                request, "request_download", _Arguments(request), cancellationToken).ConfigureAwait(false));
            return _String(document.RootElement, "downloadUrl") is { Length: > 0 } url
                ? url
                : throw new _Failure(DepotBrowserStatus.Error, "Depot did not return a download URL.");
        });

    // A write, so it needs the Editor role: checked here against Depot's own role for the caller, not left to the UI.
    public Task<DepotBrowserAnswer<DepotBrowserShare>> ShareAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = _Required(request.Project, "project");
            var path = _Required(request.Path, "path");
            if (request.Mode is not ("members" or "public"))
            {
                throw new _Failure(DepotBrowserStatus.Error, "Choose Members only or Public.");
            }

            await _RequireEditorAsync(request, project, cancellationToken).ConfigureAwait(false);
            using var document = _Parse(await _CallAsync(
                request,
                "share_artifact",
                new Dictionary<string, object?> { ["project"] = project, ["path"] = path, ["mode"] = request.Mode },
                cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (_String(root, "shareId") is { Length: > 0 } id && _String(root, "url") is { Length: > 0 } url)
            {
                return new DepotBrowserShare(id, url, root.TryGetProperty("isPublic", out var isPublic) ? isPublic.ValueKind == JsonValueKind.True : request.Mode == "public");
            }

            // The answer carried no link: the project's share list is where Depot says what exists.
            var found = (await _SharesAsync(request, project, cancellationToken).ConfigureAwait(false)).FirstOrDefault(share => share.Path == path)
                ?? throw new _Failure(DepotBrowserStatus.Error, "Depot did not return the share link.");
            return new DepotBrowserShare(found.ShareId, found.Url, found.IsPublic);
        });

    public Task<DepotBrowserAnswer<bool>> UnshareAsync(DepotBrowserRequest request, CancellationToken cancellationToken) =>
        _RunAsync(async () =>
        {
            var project = _Required(request.Project, "project");
            var shareId = _Required(request.ShareId, "shareId");
            await _RequireEditorAsync(request, project, cancellationToken).ConfigureAwait(false);
            await _CallAsync(
                request,
                "unshare_artifact",
                new Dictionary<string, object?> { ["project"] = project, ["shareId"] = shareId },
                cancellationToken).ConfigureAwait(false);
            return true;
        });

    private sealed record _Share(string ShareId, string Path, string Url, bool IsPublic);

    private async Task<IReadOnlyList<_Share>> _SharesAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken)
    {
        using var document = _Parse(await _CallAsync(
            request, "list_shares", new Dictionary<string, object?> { ["project"] = project }, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("shares", out var shares) || shares.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. shares.EnumerateArray()
            .Where(share => _String(share, "shareId") is not null && _String(share, "path") is not null && _String(share, "url") is not null)
            .Select(share => new _Share(
                _String(share, "shareId")!,
                _String(share, "path")!,
                _String(share, "url")!,
                share.TryGetProperty("isPublic", out var isPublic) && isPublic.ValueKind == JsonValueKind.True))];
    }

    private async Task _RequireEditorAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken)
    {
        if (!(await _RoleAsync(request, project, cancellationToken).ConfigureAwait(false)).CanWrite())
        {
            throw new _Failure(DepotBrowserStatus.Error, "Sharing needs the Editor role in this project.");
        }
    }

    private async Task<DepotProjectRole> _RoleAsync(DepotBrowserRequest request, string project, CancellationToken cancellationToken) =>
        (await _ListProjectsAsync(request, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(row => row.Argument == project)?.Role ?? DepotProjectRole.Unknown;

    private async Task<IReadOnlyList<_Row>> _ListProjectsAsync(DepotBrowserRequest request, CancellationToken cancellationToken)
    {
        using var document = _Parse(await _CallAsync(request, "list_projects", null, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. projects.EnumerateArray()
            .Select(project => (Slug: _String(project, "slug"), Address: _String(project, "address"), Project: project))
            .Where(row => row.Slug is not null)
            .Select(row => new _Row(
                row.Address ?? row.Slug!,
                _String(row.Project, "name") ?? row.Slug!,
                DepotProjectRoleParser.Parse(_String(row.Project, "role"))))];
    }

    private async Task<string> _CallAsync(
        DepotBrowserRequest request, string tool, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == request.ConnectionId)
            ?? throw new _Failure(DepotBrowserStatus.Error, "That Depot connection is no longer configured.");
        var result = await host.CallMcpToolAsync(connection.McpServerName, tool, arguments, projectId: null, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            PluginMcpToolCallOutcome.Success => result.Content ?? string.Empty,
            PluginMcpToolCallOutcome.AuthorizationRequired => throw new _Failure(DepotBrowserStatus.SignIn, $"Sign in to {connection.Name}"),
            _ => throw new _Failure(DepotBrowserStatus.Error, result.Error is { Length: > 0 } error ? error : "Depot did not answer."),
        };
    }

    private static async Task<DepotBrowserAnswer<T>> _RunAsync<T>(Func<Task<T>> body)
    {
        try
        {
            return DepotBrowserAnswer<T>.Success(await body().ConfigureAwait(false));
        }
        catch (_Failure failure)
        {
            return DepotBrowserAnswer<T>.Failure(failure.Status, failure.Message);
        }
    }

    private static Dictionary<string, object?> _Arguments(DepotBrowserRequest request) => new()
    {
        ["project"] = _Required(request.Project, "project"),
        ["path"] = _Required(request.Path, "path"),
    };

    private static string _Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new _Failure(DepotBrowserStatus.Error, $"The request names no {name}.") : value;

    private static JsonDocument _Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new _Failure(DepotBrowserStatus.Error, "Depot's answer came back in an unexpected shape.");
        }
    }

    private static IEnumerable<JsonElement> _Entries(JsonElement root) =>
        root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray() : [];

    private static DepotBrowserNode _Node(JsonElement entry, string kind) => new(
        _String(entry, "path") ?? string.Empty,
        kind,
        entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0,
        DateTimeOffset.TryParse(_String(entry, "updatedAt"), out var modified) ? modified : null,
        _String(entry, "contentType"));

    private static string? _String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
