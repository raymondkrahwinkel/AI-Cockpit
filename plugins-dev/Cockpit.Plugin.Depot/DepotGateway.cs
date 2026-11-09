using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot;

// AC-2217: one extra gateway MCP row per connection, on its sign-in. Best effort and never throws: no answer = no row, no extra scope.
internal sealed class DepotGateway(ICockpitHost host, ILogger? logger = null) : IDisposable
{
    internal const string Scope = "depot.gateway";

    // The one place that names the contract's fields (Depot `Docs/mcp-gateway-cockpit-contract.md`): the discovery route
    // and the top-level address in its answer. The per-organization status in that answer is not read here.
    private const string DiscoveryPath = "cockpit/gateway";
    private const string AddressField = "gatewayUrl";

    private const string RowPrefix = "Depot gateway: ";

    // The resource metadata of the gateway lists `depot.gateway`; a Depot without the gateway answers for /mcp/gateway with
    // the plain `depot` scope or not at all (contract, § Gateway-authenticatie).
    private const string MetadataPath = "/.well-known/oauth-protected-resource/mcp/gateway";

    private static readonly TimeSpan _scopeStale = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, _State> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record _State(bool ScopeSupported, string? Url, DateTimeOffset ScopeReadAt);

    internal static bool IsGatewayServerName(string serverName) => serverName.StartsWith(RowPrefix, StringComparison.Ordinal);

    internal bool ScopeSupported(DepotConnectionRegistration connection) =>
        _states.TryGetValue(connection.Id, out var state) && state.ScopeSupported;

    // Sign-in reads the contribution synchronously, so the scope check just before it is waited on, briefly. A scope read
    // in the last minute is still current; an unreachable Depot keeps the last answer. ponytail: a blocking wait of at
    // most 2 s on the sign-in path, because the host asks for the contribution synchronously.
    internal void EnsureScopeFresh(DepotConnectionRegistration connection)
    {
        if (_states.TryGetValue(connection.Id, out var state) && DateTimeOffset.UtcNow - state.ScopeReadAt < _scopeStale)
        {
            return;
        }

        try
        {
            Task.Run(() => RefreshScopeAsync(connection, CancellationToken.None)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            logger?.LogDebug(exception, "Reading the Depot gateway scope for {Connection} before signing in failed.", connection.Name);
        }
    }

    internal async Task RefreshAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshScopeAsync(connection, cancellationToken).ConfigureAwait(false);
            await _RefreshAddressAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogDebug(exception, "Reading the Depot gateway of {Connection} failed.", connection.Name);
        }
    }

    internal async Task RefreshScopeAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        var origin = DepotUrlNormalizer.Origin(connection.Url);
        if (origin is null)
        {
            return;
        }

        bool supported;
        try
        {
            using var response = await _http.GetAsync(origin + MetadataPath, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
            {
                return;
            }

            supported = response.IsSuccessStatusCode && _Lists(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), Scope);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException
            && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable: the last answer of this run stays.
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var previous = _states.GetValueOrDefault(connection.Id);
            if (previous?.ScopeSupported != supported)
            {
                logger?.LogInformation("{Connection} {Verdict} the Depot gateway.", connection.Name, supported ? "offers" : "does not offer");
            }

            _states[connection.Id] = new _State(supported, supported ? previous?.Url : null, DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _RefreshAddressAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        if (!ScopeSupported(connection))
        {
            return;
        }

        string? url;
        try
        {
            using var api = new DepotApi(host, connection);
            var result = await api.GetAsync(DiscoveryPath, cancellationToken).ConfigureAwait(false);
            url = result.Outcome == DepotApiOutcome.Success ? ParseAddress(result.Content.Span, connection) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_states.TryGetValue(connection.Id, out var state))
            {
                _states[connection.Id] = state with { Url = url };
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The connection's one gateway row, at the address the answer gives — never built here. Named after the connection.
    internal IEnumerable<McpServerContribution> ContributionsFor(
        DepotConnectionRegistration connection, string oauthAuthority, string oauthScopes, string hostOnlyScopes)
    {
        if (!_states.TryGetValue(connection.Id, out var state) || !state.ScopeSupported || state.Url is null)
        {
            return [];
        }

        return
        [
            new McpServerContribution(Name: RowPrefix + connection.Name, Url: state.Url)
            {
                Id = connection.Id,
                IncludeAgentName = true,
                OAuthAuthority = oauthAuthority,
                OAuthClientId = "depot",
                OAuthScopes = oauthScopes,
                OAuthHostOnlyScopes = hostOnlyScopes,
            },
        ];
    }

    private static bool _Lists(string metadata, string scope)
    {
        using var document = JsonDocument.Parse(metadata);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("scopes_supported", out var scopes)
            && scopes.ValueKind == JsonValueKind.Array
            && scopes.EnumerateArray().Any(entry => entry.ValueKind == JsonValueKind.String && entry.GetString() == scope);
    }

    // An unexpected answer is no answer: no row. The address must be an absolute one on the connection's own origin.
    internal static string? ParseAddress(ReadOnlySpan<byte> content, DepotConnectionRegistration connection)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(AddressField, out var url)
                || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address)
                || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps)
                || !string.Equals(address.GetLeftPart(UriPartial.Authority), DepotUrlNormalizer.Origin(connection.Url), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return url.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
