using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1474: the remote side treats PluginStoreConfig.Location only as the opaque server store id.
internal sealed class RemotePluginAdministration(BackendApiClient client, ILogger? logger = null) : IPluginAdministration, IPluginStoreCatalog, IPluginChannelRelay, IServerRestart
{
    public async Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var entries = await client.SendAsync<IReadOnlyList<RemotePluginEntry>>(HttpMethod.Get, "api/v1/plugins", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        return entries.Select(RemotePluginEntry.ToInstalled).ToList();
    }

    public async Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default)
    {
        var catalog = await GetStoresAsync(cancellationToken).ConfigureAwait(false);
        var selected = catalog.FirstOrDefault(candidate => string.Equals(candidate.Id, store.Location, StringComparison.Ordinal));
        return selected?.Index is { } index
            ? new PluginStoreFetchResult(true, null, index, selected.Location)
            : new PluginStoreFetchResult(false, "That plugin store is not configured on the server.", null, null);
    }

    public Task<IReadOnlyList<PluginStoreCatalogEntry>> GetStoresAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<PluginStoreCatalogEntry>>(HttpMethod.Get, "api/v1/plugins/store", null, ConnectKeyEndpoints.Json, cancellationToken);

    public Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Install from the server's store, or copy the zip onto the server.");

    public async Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await client.SendAsync<RemotePluginInstall>(HttpMethod.Post, "api/v1/plugins", new { storeId = request.Store.Location, pluginId = request.Id, version = request.Version.Version }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        return new PluginProvisionResult(response.Outcome, response.Id, response.Name, response.Error, null, response.FolderId, response.Sha256);
    }

    public async Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Put, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/enabled", new { enabled, sha256 = pinnedSha256 }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Delete, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    public Task<JsonElement> InvokeChannelAsync(string folderId, string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        client.SendAsync<JsonElement>(HttpMethod.Post, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/channel/{Uri.EscapeDataString(action)}", payload, ConnectKeyEndpoints.Json, cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetStorageAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var slice = await client.SendAsync<JsonObject>(HttpMethod.Get, _StoragePath(folderId), null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        return slice.ToDictionary(property => property.Key, property => property.Value?.ToJsonString() ?? "null", StringComparer.Ordinal);
    }

    public async Task SaveStorageAsync(
        string folderId,
        IReadOnlyDictionary<string, string> changed,
        IReadOnlyCollection<string> removed,
        IReadOnlyCollection<string> secrets,
        CancellationToken cancellationToken = default)
    {
        var values = new JsonObject();
        foreach (var (key, json) in changed)
        {
            values[key] = JsonNode.Parse(json);
        }

        await client.SendAsync<JsonObject>(HttpMethod.Put, _StoragePath(folderId), new { values, removed, secrets }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetMcpServerAuthStateAsync(string folderId, string name, CancellationToken cancellationToken = default)
    {
        var answer = await client.SendAsync<RemoteAuthState>(
            HttpMethod.Get,
            $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/mcp-servers/{Uri.EscapeDataString(name)}/auth-state",
            null,
            ConnectKeyEndpoints.Json,
            cancellationToken).ConfigureAwait(false);
        return answer.State;
    }

    private sealed record RemoteAuthState(string State);

    // AC-1571 B2: the listener is open before the server hears its port, and the browser only gets a link whose redirect
    // comes back to it. The redirect's query goes to the server, which redeems the code with the verifier it kept.
    // A failure answers Unreachable rather than throwing: the view awaits this in a click handler (see the local host).
    public async Task<string> SignInMcpServerAsync(string folderId, string name, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _SignInMcpServerAsync(folderId, name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("The sign-in for MCP server {Server} on the server failed ({Error}).", name, exception.GetType().Name);
            return "Unreachable";
        }
    }

    private async Task<string> _SignInMcpServerAsync(string folderId, string name, CancellationToken cancellationToken)
    {
        using var loopback = McpOAuthLoopback.Open(_logger);
        if (loopback is null)
        {
            return "Unavailable";
        }

        var path = $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/mcp-servers/{Uri.EscapeDataString(name)}/sign-in";
        var flow = await client.SendAsync<RemoteMcpSignIn>(HttpMethod.Post, path, new { port = loopback.Port }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        var flowPath = $"{path}/{Uri.EscapeDataString(flow.FlowId)}";
        var handedBack = false;
        try
        {
            if (flow.AwaitsRedirect
                && _LinkBackTo(flow.Url, loopback.RedirectUri) is { } link
                && McpOAuthLoopback.StateOf(link) is { } state
                && (BrowserOpener ?? (url => McpOAuthLoopback.OpenBrowser(url, _logger)))(link))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Never longer than the server keeps a sign-in, nor negative, whatever this desktop's clock says.
                var left = flow.ExpiresAt - DateTimeOffset.UtcNow;
                deadline.CancelAfter(left > TimeSpan.Zero && left < McpServerSignIns.Lifetime ? left : McpServerSignIns.Lifetime);
                if (await loopback.ReceiveAsync(name, state, deadline.Token).ConfigureAwait(false) is { } redirect)
                {
                    flow = await client.SendAsync<RemoteMcpSignIn>(HttpMethod.Post, $"{flowPath}/input", new { text = redirect }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
                    handedBack = true;
                }
            }

            while (handedBack && flow.Status == "running")
            {
                await Task.Delay(SignInPoll, cancellationToken).ConfigureAwait(false);
                flow = await client.SendAsync<RemoteMcpSignIn>(HttpMethod.Get, flowPath, null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // A sign-in this end will not finish is ended now, so the server puts its old token back rather than in ten minutes.
            if (flow.Status == "running" && !handedBack)
            {
                await _EndSignInAsync(flowPath).ConfigureAwait(false);
            }
        }

        return flow.Status switch
        {
            "succeeded" => "Authorized",
            "unavailable" => "Unavailable",
            "unreachable" => "Unreachable",
            _ => "Declined",
        };
    }

    private async Task _EndSignInAsync(string flowPath)
    {
        try
        {
            await client.SendAsync<JsonObject>(HttpMethod.Delete, flowPath, null, ConnectKeyEndpoints.Json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Could not end an unfinished MCP sign-in on the server ({Error}); it ends there on its own.", exception.GetType().Name);
        }
    }

    // The server's link only when it is http(s) and redirects to this machine's own listener, so the browser is never
    // sent anywhere that hands the code to someone else.
    private static Uri? _LinkBackTo(string? url, Uri redirectUri) =>
        Uri.TryCreate(url, UriKind.Absolute, out var link)
        && link.Scheme is "https" or "http"
        && System.Web.HttpUtility.ParseQueryString(link.Query)["redirect_uri"] == redirectUri.AbsoluteUri
            ? link
            : null;

    private static readonly TimeSpan SignInPoll = TimeSpan.FromMilliseconds(250);

    // Stands in for the system browser, which a test run must never open on the machine running it.
    internal Func<Uri, bool>? BrowserOpener { get; set; }

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    private sealed record RemoteMcpSignIn(string FlowId, string Status, string? Url, bool AwaitsRedirect, DateTimeOffset ExpiresAt);

    public Task<IReadOnlyDictionary<string, string?>> GetManagedClisAsync(string folderId, CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyDictionary<string, string?>>(HttpMethod.Get, _ManagedCliPath(folderId, null), null, ConnectKeyEndpoints.Json, cancellationToken);

    public Task<RemoteManagedCliStatus> GetManagedCliStatusAsync(string folderId, string cliName, CancellationToken cancellationToken = default) =>
        client.SendAsync<RemoteManagedCliStatus>(HttpMethod.Get, _ManagedCliPath(folderId, cliName) + "/status", null, ConnectKeyEndpoints.Json, cancellationToken);

    public Task<RemoteManagedCliInstall> InstallManagedCliAsync(string folderId, string cliName, CancellationToken cancellationToken = default) =>
        client.SendAsync<RemoteManagedCliInstall>(HttpMethod.Post, _ManagedCliPath(folderId, cliName) + "/install", null, ConnectKeyEndpoints.Json, cancellationToken);

    public async Task<bool> RemoveManagedCliAsync(string folderId, string cliName, CancellationToken cancellationToken = default) =>
        (await client.SendAsync<RemoteRemoved>(HttpMethod.Delete, _ManagedCliPath(folderId, cliName), null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false)).Removed;

    public async Task SetManagedCliAutoUpdateAsync(string folderId, string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        await client.SendAsync<JsonObject>(HttpMethod.Put, _ManagedCliPath(folderId, cliName) + "/auto-update", new { enabled }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);

    private sealed record RemoteRemoved(bool Removed);

    private static string _ManagedCliPath(string folderId, string? cliName) =>
        $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/managed-clis" + (cliName is null ? "" : $"/{Uri.EscapeDataString(cliName)}");

    private static string _StoragePath(string folderId) => $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/storage";

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/server/restart", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record RemotePluginInstall(PluginProvisionOutcome Outcome, string Id, string Name, string Version, string? Error, string? FolderId, string? Sha256);

    // AC-1525: the server's decision and its hash of the bytes, so enabling walks the same consent as a local plugin. The
    // folder's path stays on the server.
    private sealed record RemotePluginEntry(
        string FolderId,
        string Id,
        string Name,
        string Version,
        bool Enabled,
        string? AvailableUpdate,
        bool TakesEffectAfterServerRestarts,
        PluginLoadDecision Decision,
        string Sha256,
        string? PinnedSha256,
        string? Author,
        string? Description,
        string? MinHostVersion)
    {
        public static InstalledPlugin ToInstalled(RemotePluginEntry entry) => new(
            new DiscoveredPlugin("", entry.FolderId, new PluginManifest(entry.Id, entry.Name, entry.Version, null, 0, null, entry.MinHostVersion, entry.Description, entry.Author), entry.Sha256, entry.Decision),
            entry.PinnedSha256 is { } pinned ? new PluginRegistration(entry.Enabled, pinned) : null, null, null, null);
    }

}
