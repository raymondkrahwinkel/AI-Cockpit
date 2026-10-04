using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1474: the remote side treats PluginStoreConfig.Location only as the opaque server store id.
internal sealed class RemotePluginAdministration(BackendApiClient client) : IPluginAdministration
{
    public Task<IReadOnlyList<InstalledPlugin>> GetInstalledAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<InstalledPlugin>>(HttpMethod.Get, "api/v1/plugins", null, ConnectKeyEndpoints.Json, cancellationToken);

    public async Task<PluginStoreFetchResult> FetchStoreIndexAsync(PluginStoreConfig store, CancellationToken cancellationToken = default)
    {
        var catalog = await client.SendAsync<IReadOnlyList<RemoteStore>>(HttpMethod.Get, "api/v1/plugins/store", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        var selected = catalog.FirstOrDefault(candidate => string.Equals(candidate.Id, store.Location, StringComparison.Ordinal));
        return selected?.Index is { } index
            ? new PluginStoreFetchResult(true, null, index, selected.Location)
            : new PluginStoreFetchResult(false, "That plugin store is not configured on the server.", null, null);
    }

    public Task<PluginInstallResult> InstallFromZipAsync(string zipFilePath, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Install from the server's store, or copy the zip onto the server.");

    public Task<PluginProvisionResult> InstallFromStoreAsync(PluginProvisionRequest request, CancellationToken cancellationToken = default) =>
        client.SendAsync<PluginProvisionResult>(HttpMethod.Post, "api/v1/plugins", new { storeId = request.Store.Location, pluginId = request.Id, version = request.Version.Version }, ConnectKeyEndpoints.Json, cancellationToken);

    public async Task SetEnabledAsync(string folderId, bool enabled, string pinnedSha256, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Put, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}/enabled", new { enabled }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string folderId, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Delete, $"api/v1/plugins/{Uri.EscapeDataString(folderId)}", null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
    }

    private sealed record RemoteStore(string Id, string Location, PluginStoreIndex? Index);
}
