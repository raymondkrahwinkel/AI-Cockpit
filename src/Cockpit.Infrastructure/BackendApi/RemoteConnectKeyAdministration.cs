using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1446: a server's key administration over its backend API. The server stamps the actor from this connection's key,
// so nothing here names one; its refusals come back as the contract's own exceptions.
internal sealed class RemoteConnectKeyAdministration(BackendApiClient client) : IConnectKeyAdministration
{
    public Task<ConnectKeyOverview> ListAsync(CancellationToken cancellationToken = default) =>
        _CallAsync<ConnectKeyOverview>(HttpMethod.Get, "api/v1/keys", null, cancellationToken);

    public Task<IssuedConnectKey> IssueAsync(ConnectKeyRequest request, CancellationToken cancellationToken = default) =>
        _CallAsync<IssuedConnectKey>(HttpMethod.Post, "api/v1/keys", request, cancellationToken);

    public Task<bool> RevokeAsync(string prefix, CancellationToken cancellationToken = default) =>
        _FoundAsync(HttpMethod.Delete, $"api/v1/keys/{Uri.EscapeDataString(prefix)}", null, cancellationToken);

    public Task<bool> SetScopeAsync(string prefix, ConnectKeyScope scope, CancellationToken cancellationToken = default) =>
        _FoundAsync(HttpMethod.Put, $"api/v1/keys/{Uri.EscapeDataString(prefix)}/scope", scope, cancellationToken);

    public Task<bool> LiftLockoutAsync(string address, CancellationToken cancellationToken = default) =>
        _FoundAsync(HttpMethod.Post, $"api/v1/lockouts/{Uri.EscapeDataString(address)}/lift", null, cancellationToken);

    public Task<IReadOnlyList<ConnectKeyAuditEntry>> ReadAuditAsync(long? before, int count, CancellationToken cancellationToken = default)
    {
        var query = before is { } id ? $"&before={id.ToString(CultureInfo.InvariantCulture)}" : "";
        return _CallAsync<IReadOnlyList<ConnectKeyAuditEntry>>(HttpMethod.Get, $"api/v1/audit?count={count}{query}", null, cancellationToken);
    }

    private async Task<bool> _FoundAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            await _CallAsync<JsonObject>(method, path, body, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task<T> _CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync<T>(method, path, body, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(exception.Description, exception);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.BadRequest)
        {
            throw new ArgumentException(exception.Description, exception);
        }
    }
}
