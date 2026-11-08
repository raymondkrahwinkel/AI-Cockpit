using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Plugins;

namespace Cockpit.Plugin.Depot.Store;

// AC-1975: one plugin-store source per signed-in Depot and organization with a store (GET /cockpit/stores); the sign-in is the credential.
internal sealed partial class DepotPluginStoreSources(
    ICockpitHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, DepotApi>? apiFor = null) : IPluginStoreSourceProvider, IDisposable
{
    // ponytail: a fixed poll picks up a new membership without a restart; an event from Depot would make it instant.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<PluginStoreSourceDescriptor>> _byConnection = [];
    private readonly Dictionary<string, PluginStoreSourceDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Timer? _timer;
    private volatile IReadOnlyList<PluginStoreSourceDescriptor> _sources = [];

    public IReadOnlyList<PluginStoreSourceDescriptor> Sources => _sources;

    public event EventHandler? SourcesChanged;

    public void Start() => _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, PollInterval);

    public async Task RefreshAsync()
    {
        try
        {
            await _refreshing.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var current = connections();
            var changed = false;
            foreach (var gone in _byConnection.Keys.Except(current.Select(connection => connection.Id)).ToList())
            {
                changed |= _byConnection.Remove(gone);
            }

            foreach (var connection in current)
            {
                if (await _ReadAsync(connection).ConfigureAwait(false) is { } sources)
                {
                    changed |= !_byConnection.TryGetValue(connection.Id, out var before) || !before.SequenceEqual(sources);
                    _byConnection[connection.Id] = sources;
                }
            }

            _sources = [.. _byConnection.Values.SelectMany(sources => sources)];
            if (changed)
            {
                SourcesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _refreshing.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer?.Dispose();
        _stop.Dispose();
    }

    // Null keeps what was known: an unreachable Depot must not take its organizations away, and the others carry on.
    private async Task<IReadOnlyList<PluginStoreSourceDescriptor>?> _ReadAsync(DepotConnectionRegistration connection)
    {
        try
        {
            using var api = apiFor?.Invoke(connection) ?? new DepotApi(host, connection);
            var result = await api.GetAsync("cockpit/stores", _stop.Token).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case DepotApiOutcome.Success:
                    return _Descriptors(connection, result.Content);
                case DepotApiOutcome.NotSignedIn or DepotApiOutcome.Forbidden or DepotApiOutcome.OlderDepot:
                    return [];
                default:
                    return null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private IReadOnlyList<PluginStoreSourceDescriptor> _Descriptors(DepotConnectionRegistration connection, ReadOnlyMemory<byte> content)
    {
        var baseUrl = connection.Url.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address))
        {
            return [];
        }

        var list = new List<PluginStoreSourceDescriptor>();
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("stores", out var stores) || stores.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var store in stores.EnumerateArray())
        {
            var organization = store.ValueKind == JsonValueKind.Object && store.TryGetProperty("organization", out var slug) && slug.ValueKind == JsonValueKind.String ? slug.GetString() : null;
            if (organization is null || !OrganizationPattern().IsMatch(organization))
            {
                continue;
            }

            var name = store.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(label.GetString()) ? label.GetString()! : organization;
            var key = $"depot:{address.Authority.ToLowerInvariant()}/{organization}";
            var indexUrl = $"{baseUrl}/cockpit/stores/{organization}/index.json";
            var serverName = connection.McpServerName;
            var cacheKey = $"{key}|{serverName}|{name}|{indexUrl}";
            if (!_descriptors.TryGetValue(cacheKey, out var descriptor))
            {
                descriptor = _descriptors[cacheKey] = new PluginStoreSourceDescriptor(key, name, indexUrl, organization, () => host.CreateAuthorizedHandler(serverName));
            }

            list.Add(descriptor);
        }

        return list;
    }

    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{0,62}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OrganizationPattern();
}
