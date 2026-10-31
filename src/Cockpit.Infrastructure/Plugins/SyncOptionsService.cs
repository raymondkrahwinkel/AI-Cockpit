using System.Text.Json.Nodes;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

// AC-1846: reads and writes `SettingsSync.Categories` for Options › Sync, next to the servers the carriers say are connected.
internal sealed class SyncOptionsService(SettingsCarriers carriers, SettingsCarrierSync sync, CockpitConfigFileAccess configFile) : ISyncOptions
{
    public async Task<SyncOverview> ReadAsync(CancellationToken cancellationToken = default)
    {
        var file = await configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var node = file?.SettingsSync;
        var categories = (node?["Categories"] as JsonObject)?.Where(pair => pair.Value is JsonArray).ToDictionary(
            pair => pair.Key,
            pair => _Ids(pair.Value),
            StringComparer.Ordinal);

        var overrides = (node?["Profiles"] as JsonObject)?.Where(pair => pair.Value is JsonArray).ToDictionary(
            pair => pair.Key,
            pair => _Ids(pair.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var servers = carriers.All
            .Select(carrier => new SyncServer(carrier.Id, carrier.DisplayName, carrier.AddressLabel, SignedIn: true))
            .ToList();
        // A server a choice names but that is not connected now is signed out; its id is all that is known of it.
        servers.AddRange((categories?.Values.SelectMany(ids => ids) ?? []).Concat(overrides.Values.SelectMany(ids => ids)).Distinct(StringComparer.Ordinal)
            .Where(id => servers.All(server => server.Id != id))
            .Select(id => new SyncServer(id, id, string.Empty, SignedIn: false)));

        var seen = (node?["Seen"] as JsonArray)?.Select(id => id?.GetValue<string>()).OfType<string>().ToArray() ?? [];
        return new SyncOverview(servers, categories, file?.Profiles.Count ?? 0, node?["Reviewed"]?.GetValue<bool>() ?? false, seen, sync.Standing, overrides);
    }

    public Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, IReadOnlyDictionary<string, IReadOnlyList<string>> profileOverrides, IReadOnlyList<string> seenServers, CancellationToken cancellationToken = default) =>
        configFile.UpdateAsync(file =>
        {
            var node = file.SettingsSync ??= new JsonObject();
            node["Categories"] = new JsonObject(categories.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, new JsonArray([.. pair.Value.Select(id => (JsonNode?)JsonValue.Create(id))]))));
            // No override at all is no key: a profile without a choice follows its category.
            if (profileOverrides.Count == 0)
            {
                node.Remove("Profiles");
            }
            else
            {
                node["Profiles"] = new JsonObject(profileOverrides.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, new JsonArray([.. pair.Value.Select(id => (JsonNode?)JsonValue.Create(id))]))));
            }
            node["Reviewed"] = true;
            node["Seen"] = new JsonArray([.. seenServers.Select(id => (JsonNode?)JsonValue.Create(id))]);
        }, cancellationToken);

    private static IReadOnlyList<string> _Ids(JsonNode? node) =>
        node is JsonArray ids ? [.. ids.Select(id => id?.GetValue<string>()).OfType<string>()] : [];
}
