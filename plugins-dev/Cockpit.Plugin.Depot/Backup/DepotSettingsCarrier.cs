using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Settings;

namespace Cockpit.Plugin.Depot.Backup;

// AC-1510: a connection's account space in Depot (DEP-168, /cockpit/profile) as the carrier of the cockpit's portable
// settings. Depot versions the document by the SHA-256 of its bytes and refuses a write whose If-Match is stale (412),
// which is what WriteIfUnchangedAsync needs. The host's bearer goes on every request; this plugin never holds a token.
internal sealed class DepotSettingsCarrier(
    ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    string connectionId,
    Func<HttpMessageHandler>? handler = null)
{
    public SettingsCarrierRegistration Registration(DepotConnectionRegistration connection) => new(
        $"depot:{connectionId}",
        $"Depot: {connection.Name}",
        connection.Url,
        async cancellationToken => await ReadAsync(cancellationToken).ConfigureAwait(false) is not null,
        ReadAsync,
        WriteAsync)
    {
        WriteIfUnchangedAsync = WriteIfUnchangedAsync,
    };

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var connection = _Required();
        if (!await _IsAuthorizedAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        using var http = _Client(connection);
        using var response = await http.GetAsync(_Profile(connection), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        _Ensure(response, connection);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> WriteIfUnchangedAsync(byte[] blob, string? baseVersion, CancellationToken cancellationToken)
    {
        var connection = _Required();
        if (!await _IsAuthorizedAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        using var http = _Client(connection);
        using var request = new HttpRequestMessage(HttpMethod.Put, _Profile(connection)) { Content = new ByteArrayContent(blob) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        // Depot asks every write what it replaces: the version last read, or that there was none.
        if (baseVersion is null)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
        else
        {
            request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{baseVersion}\""));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }

        _Ensure(response, connection);
        return true;
    }

    // The plain write still names what it replaces, as Depot requires: whatever is there right now.
    public async Task WriteAsync(byte[] blob, CancellationToken cancellationToken)
    {
        if (!await _IsAuthorizedAsync(_Required(), cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var version = current is null ? null : Convert.ToHexStringLower(SHA256.HashData(current));
        if (!await WriteIfUnchangedAsync(blob, version, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The settings in Depot changed while they were being written. Try again.");
        }
    }

    private async Task<bool> _IsAuthorizedAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken) =>
        connection.SynchroniseSettings
        && await host.GetMcpServerAuthStateAsync(connection.McpServerName, cancellationToken).ConfigureAwait(false) != PluginMcpAuthState.AuthorizationRequired;

    private DepotConnectionRegistration _Required() =>
        connections().FirstOrDefault(connection => connection.Id == connectionId)
        ?? throw new InvalidOperationException("That Depot connection is no longer configured.");

    private HttpClient _Client(DepotConnectionRegistration connection) =>
        new(handler?.Invoke() ?? host.CreateAuthorizedHandler(connection.McpServerName, "depot.host"), disposeHandler: true);

    private static string _Profile(DepotConnectionRegistration connection) => $"{connection.Url.TrimEnd('/')}/cockpit/profile";

    private static void _Ensure(HttpResponseMessage response, DepotConnectionRegistration connection)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"sign-in needed. Sign in to {connection.Name} in the Depot settings first.",
            HttpStatusCode.RequestEntityTooLarge => $"{connection.Name} refused the settings: they are larger than it accepts.",
            _ => $"{connection.Name} refused the request (HTTP {(int)response.StatusCode}).",
        });
    }
}
