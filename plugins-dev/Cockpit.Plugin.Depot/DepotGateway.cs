using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
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
    // toolLoad is opt-in (contract, § toolLoad is opt-in): 5 queries instead of 3, fine for a read that happens on sign-in and after an authorization.
    private const string DiscoveryPath = "cockpit/gateway?include=toolLoad";
    private const string AddressField = "gatewayUrl";

    // The same answer lists the personal MCPs, under each organization it names.
    private const string OrganizationsField = "organizations";
    private const string PersonalField = "personalConnections";

    private const string RowPrefix = "Depot gateway: ";

    // The resource metadata of the gateway lists `depot.gateway`; a Depot without the gateway answers for /mcp/gateway with
    // the plain `depot` scope or not at all (contract, § Gateway-authenticatie).
    private const string MetadataPath = "/.well-known/oauth-protected-resource/mcp/gateway";

    private static readonly TimeSpan _scopeStale = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<string, _State> _states = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record _State(bool ScopeSupported, string? Url, DateTimeOffset ScopeReadAt, IReadOnlyList<DepotPersonalMcp> Personal);

    internal static bool IsGatewayServerName(string serverName) => serverName.StartsWith(RowPrefix, StringComparison.Ordinal);

    internal bool ScopeSupported(DepotConnectionRegistration connection) =>
        _states.TryGetValue(connection.Id, out var state) && state.ScopeSupported;

    // What the last read of the discovery said across the organizations; empty when it said nothing.
    internal IReadOnlyList<DepotPersonalMcp> PersonalFor(DepotConnectionRegistration connection) =>
        _states.TryGetValue(connection.Id, out var state) ? state.Personal : [];

    // The name the host knows this connection's gateway row by, for RefreshMcpServerAsync.
    internal static string ServerNameFor(DepotConnectionRegistration connection) => RowPrefix + connection.Name;

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
            await RefreshAddressAsync(connection, cancellationToken).ConfigureAwait(false);
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

            _states[connection.Id] = new _State(supported, supported ? previous?.Url : null, DateTimeOffset.UtcNow, supported ? previous?.Personal ?? [] : []);
        }
        finally
        {
            _gate.Release();
        }
    }

    // One discovery call gives the gateway address and the personal MCPs; internal so the wait after an authorization can repeat it.
    internal async Task RefreshAddressAsync(DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        if (!ScopeSupported(connection))
        {
            host.SetMcpServerAdvisory(ServerNameFor(connection), null);
            return;
        }

        string? url;
        IReadOnlyList<DepotPersonalMcp>? personal = null;
        try
        {
            using var api = new DepotApi(host, connection);
            var result = await api.GetAsync(DiscoveryPath, cancellationToken).ConfigureAwait(false);
            var success = result.Outcome == DepotApiOutcome.Success;
            url = success ? ParseAddress(result.Content.Span, connection) : null;
            var foreign = 0;
            personal = success ? ParsePersonal(result.Content.Span, connection, out foreign) : null;
            if (success)
            {
                host.SetMcpServerAdvisory(ServerNameFor(connection), url is null ? null : ParseAdvisory(result.Content.Span, connection));
            }
            if (foreign > 0)
            {
                logger?.LogDebug("{Connection} lists {Count} personal MCP(s) with an authorize address outside its own origin; they are left out.", connection.Name, foreign);
            }
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
                _states[connection.Id] = state with { Url = url, Personal = personal ?? state.Personal };
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
            new McpServerContribution(Name: ServerNameFor(connection), Url: state.Url)
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

    // The load of the one merged tool list, as Depot totals it: nothing is added up here. No toolLoad (a closed or older
    // Depot) or Ok is no advisory; the link goes to the Performance page of the first organization that takes part.
    internal static PluginMcpAdvisory? ParseAdvisory(ReadOnlySpan<byte> content, DepotConnectionRegistration connection)
    {
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("toolLoad", out var load)
                || load.ValueKind != JsonValueKind.Object
                || !load.TryGetProperty("toolCount", out var count) || !count.TryGetInt32(out var tools)
                || !load.TryGetProperty("schemaBytes", out var size) || !size.TryGetInt64(out var bytes)
                || _Text(load, "level") is not { } level || (level != "Warning" && level != "Critical"))
            {
                return null;
            }

            string? link = null;
            if (document.RootElement.TryGetProperty(OrganizationsField, out var organizations) && organizations.ValueKind == JsonValueKind.Array
                && organizations.EnumerateArray().FirstOrDefault(organization => organization.ValueKind == JsonValueKind.Object
                    && organization.TryGetProperty("gatewayAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.True) is { ValueKind: JsonValueKind.Object } first
                && _Text(first, "slug") is { Length: > 0 } slug && DepotUrlNormalizer.Origin(connection.Url) is { } origin)
            {
                link = $"{origin}/org/{Uri.EscapeDataString(slug)}/mcp/performance";
            }

            var kilobytes = (bytes / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            return new(
                level == "Critical" ? PluginMcpAdvisoryLevel.Critical : PluginMcpAdvisoryLevel.Warning,
                $"{tools} tools, {kilobytes} KB of tool descriptions. Agents get slower and less precise with this many.",
                link);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A foreign-origin authorize address is dropped, since the browser is opened on it; `foreign` counts them so it can be logged.
    internal static IReadOnlyList<DepotPersonalMcp>? ParsePersonal(ReadOnlySpan<byte> content, DepotConnectionRegistration connection, out int foreign)
    {
        foreign = 0;
        try
        {
            using var document = JsonDocument.Parse(content.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(OrganizationsField, out var organizations)
                || organizations.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var origin = DepotUrlNormalizer.Origin(connection.Url);
            List<DepotPersonalMcp> found = [];
            foreach (var organization in organizations.EnumerateArray())
            {
                if (organization.ValueKind != JsonValueKind.Object
                    || !organization.TryGetProperty(PersonalField, out var entries)
                    || entries.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object
                        && _Text(entry, "id") is { Length: > 0 } id
                        && _Text(entry, "name") is { Length: > 0 } name
                        && _Text(entry, "status") is (DepotPersonalStatus.NotAuthorized or DepotPersonalStatus.Active or DepotPersonalStatus.Expired or DepotPersonalStatus.Unreachable) and var status
                        && _Text(entry, "authorizeUrl") is { } authorizeUrl
                        && Uri.TryCreate(authorizeUrl, UriKind.Absolute, out var address)
                        && !found.Any(other => other.Id == id))
                    {
                        if (string.Equals(address.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add(new DepotPersonalMcp(id, name, status, authorizeUrl));
                        }
                        else
                        {
                            foreign++;
                        }
                    }
                }
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? _Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
