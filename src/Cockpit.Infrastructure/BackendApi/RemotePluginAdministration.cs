using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1474: the remote side treats PluginStoreConfig.Location only as the opaque server store id.
internal sealed class RemotePluginAdministration(BackendApiClient client) : IPluginAdministration, IPluginStoreCatalog
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
        return new PluginProvisionResult(response.Outcome, response.Id, response.Name, response.Error, null, null, null);
    }

    public async Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Put, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/enabled", new { enabled }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Delete, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record RemotePluginInstall(PluginProvisionOutcome Outcome, string Id, string Name, string Version, string? Error);

    private sealed record RemotePluginEntry(string FolderId, string Id, string Name, string Version, bool Enabled, string? AvailableUpdate, bool TakesEffectAfterServerRestarts)
    {
        public static InstalledPlugin ToInstalled(RemotePluginEntry entry) => new(
            new DiscoveredPlugin("", entry.FolderId, new PluginManifest(entry.Id, entry.Name, entry.Version, null, 0, null, null, null, null), "", PluginLoadDecision.Load),
            new PluginRegistration(entry.Enabled, ""), null, null, null);
    }

}
