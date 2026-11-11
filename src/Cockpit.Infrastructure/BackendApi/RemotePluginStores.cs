using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;

namespace Cockpit.Infrastructure.BackendApi;

internal sealed partial class RemotePluginAdministration
{
    public event EventHandler? SourcesChanged;

    async Task<IReadOnlyList<PluginStoreConfig>> IPluginStoreConfigStore.LoadAsync(CancellationToken cancellationToken) =>
        (await GetStoresAsync(cancellationToken).ConfigureAwait(false))
            .Where(entry => entry.SourceKey is null).Select(_Store).ToList();

    async Task<IReadOnlyList<PluginStoreSourceInfo>> IPluginStoreSources.LoadAsync(CancellationToken cancellationToken) =>
        (await GetStoresAsync(cancellationToken).ConfigureAwait(false))
            .Select(entry => new PluginStoreSourceInfo(_Store(entry), entry.Name, entry.IsHidden, entry.IsWithdrawn)).ToList();

    public async Task AddAsync(PluginStoreConfig store, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Post, "api/v1/plugins/store",
            new { store.Kind, store.Location, store.Token }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(PluginStoreConfig store, CancellationToken cancellationToken = default)
    {
        await client.SendAsync<JsonObject>(HttpMethod.Delete, $"api/v1/plugins/store/{Uri.EscapeDataString(store.ServerId ?? store.Location)}",
            null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetHiddenAsync(string sourceKey, bool hidden, CancellationToken cancellationToken = default)
    {
        var source = (await GetStoresAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(entry => string.Equals(entry.SourceKey, sourceKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("That plugin source is no longer available on the server.");
        await client.SendAsync<JsonObject>(HttpMethod.Put, $"api/v1/plugins/store/{Uri.EscapeDataString(source.Id)}/hidden",
            new { hidden }, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static PluginStoreConfig _Store(PluginStoreCatalogEntry entry) =>
        new(entry.Kind, entry.Location)
        {
            ServerId = entry.Id,
            Target = entry.Target,
            Signed = entry.SourceKey is { } key && entry.Organization is { } organization
                ? new PluginStoreSignedSource(key, organization,
                    () => throw new InvalidOperationException("This plugin source is fetched only on the server."))
                : null,
        };
}
