using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot;

internal sealed record DepotBrandingSnapshot(string Name, string? Fingerprint = null, byte[]? Logo = null)
{
    public static DepotBrandingSnapshot Default { get; } = new("Depot");
}

// One branding read per connection and one downloaded asset per deployment fingerprint. The endpoint's ETag avoids
// re-reading an unchanged payload; the fingerprint is the asset identity, so a restarted white-label deployment
// cannot leave the previous logo attached to the connection.
internal sealed class DepotBranding
{
    private sealed record Cached(string? ETag, DepotBrandingSnapshot Snapshot);

    private sealed record Payload(string? Name, string? Fingerprint, Assets? Assets);

    private sealed record Assets(string? Icon, string? LogoLight, string? LogoDark);

    private static readonly ConditionalWeakTable<ICockpitHost, DepotBranding> _instances = new();
    private static readonly HttpClient _assets = new();
    private readonly Dictionary<string, Cached> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _logos = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICockpitHost _host;

    private DepotBranding(ICockpitHost host) => _host = host;

    public static DepotBranding For(ICockpitHost host) => _instances.GetValue(host, static value => new(value));

    public async Task<DepotBrandingSnapshot> GetAsync(
        DepotConnectionRegistration connection, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connectionKey = $"{connection.Id}|{connection.Url}";
            _connections.TryGetValue(connectionKey, out var cached);
            using var api = new DepotApi(_host, connection);
            var response = await api.GetAsync("cockpit/branding", cached?.ETag, cancellationToken).ConfigureAwait(false);
            if (response.Outcome == DepotApiOutcome.NotModified)
            {
                return cached?.Snapshot ?? DepotBrandingSnapshot.Default;
            }

            if (response.Outcome != DepotApiOutcome.Success
                || JsonSerializer.Deserialize<Payload>(response.Content.Span, DepotChannel.Json) is not
                    { Fingerprint: { Length: > 0 } fingerprint } payload)
            {
                return cached?.Snapshot ?? DepotBrandingSnapshot.Default;
            }

            byte[]? logo = null;
            if (_logos.TryGetValue(fingerprint, out var known))
            {
                logo = known;
            }
            else if (payload.Assets is { } assets
                     && (assets.LogoLight ?? assets.LogoDark ?? assets.Icon) is { Length: > 0 } assetUrl
                     && _IsSameOrigin(connection.Url, assetUrl, out Uri? assetUri))
            {
                using var asset = await _assets.GetAsync(assetUri, cancellationToken).ConfigureAwait(false);
                if (asset.IsSuccessStatusCode)
                {
                    var bytes = await asset.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        logo = bytes;
                        _logos[fingerprint] = logo;
                    }
                }
            }

            var snapshot = new DepotBrandingSnapshot(
                string.IsNullOrWhiteSpace(payload.Name) ? "Depot" : payload.Name.Trim(), fingerprint, logo);
            _connections[connectionKey] = new Cached(response.ETag, snapshot);
            return snapshot;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or JsonException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            return _connections.TryGetValue($"{connection.Id}|{connection.Url}", out var cached)
                ? cached.Snapshot
                : DepotBrandingSnapshot.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool _IsSameOrigin(
        string baseUrl, string assetUrl, [NotNullWhen(true)] out Uri? asset)
    {
        asset = null;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin)
            || !Uri.TryCreate(assetUrl, UriKind.Absolute, out var candidate)
            || !string.Equals(origin.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(origin.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
            || origin.Port != candidate.Port)
        {
            return false;
        }

        asset = candidate;
        return true;
    }
}
