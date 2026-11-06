using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2035: a server's Docker storage setting over its backend API.
internal sealed class RemoteServerStorageSettings(BackendApiClient client) : IServerStorageSettings
{
    private const string Route = "api/v1/storage/settings";

    public async Task<bool> GetAutomaticDockerCleanupAsync(CancellationToken cancellationToken = default) =>
        (await client.SendAsync<StorageSettingsBody>(HttpMethod.Get, Route, null, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false)).AutomaticDockerCleanup ?? false;

    public Task SetAutomaticDockerCleanupAsync(bool enabled, CancellationToken cancellationToken = default) =>
        client.SendAsync<StorageSettingsBody>(HttpMethod.Put, Route, new StorageSettingsBody(enabled), ConnectKeyEndpoints.Json, cancellationToken);
}
