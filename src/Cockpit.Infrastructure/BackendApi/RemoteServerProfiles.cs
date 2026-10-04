using System.Net;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1473: a server's profiles over its backend API. A change goes out as the fields it names, never as the list read
// back, which holds no secrets and would wipe them on the server.
internal sealed class RemoteServerProfiles(BackendApiClient client) : IServerProfiles
{
    public async Task<IReadOnlyList<RemoteProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        (await _CallAsync<RemoteProfileList>(HttpMethod.Get, "api/v1/profiles", null, cancellationToken).ConfigureAwait(false)).Profiles;

    public Task<RemoteProfile> CreateAsync(RemoteNewProfile profile, CancellationToken cancellationToken = default) =>
        _CallAsync<RemoteProfile>(HttpMethod.Post, "api/v1/profiles", profile, cancellationToken);

    public async Task<RemoteProfile?> UpdateAsync(string label, RemoteProfilePatch patch, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _CallAsync<RemoteProfile>(HttpMethod.Patch, _Path(label), patch, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string label, CancellationToken cancellationToken = default)
    {
        try
        {
            await _CallAsync<JsonObject>(HttpMethod.Delete, _Path(label), null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string _Path(string label) => $"api/v1/profiles/{Uri.EscapeDataString(label)}";

    private async Task<T> _CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync<T>(method, path, body, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw new ArgumentException(exception.Description, exception);
        }
    }

    private sealed record RemoteProfileList(IReadOnlyList<RemoteProfile> Profiles);
}
