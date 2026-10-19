using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1474: the remote side treats PluginStoreConfig.Location only as the opaque server store id.
internal sealed class RemotePluginAdministration(BackendApiClient client) : IPluginAdministration, IPluginStoreCatalog, IPluginChannelRelay, IServerRestart
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
