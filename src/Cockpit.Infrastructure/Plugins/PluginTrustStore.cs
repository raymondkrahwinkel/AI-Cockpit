using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class PluginTrustStore : IPluginTrustStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _config;

    public PluginTrustStore() : this(CockpitConfigPath.Default) { }

    internal PluginTrustStore(string configFilePath) => _config = new CockpitConfigFileAccess(configFilePath);

    public async Task<PluginTrustEntry?> GetAsync(string folderId, CancellationToken cancellationToken = default) =>
        (await _config.ReadAsync(cancellationToken).ConfigureAwait(false))?.PluginTrust.GetValueOrDefault(folderId);

    public Task SaveAsync(string folderId, PluginTrustEntry entry, CancellationToken cancellationToken = default) =>
        _config.UpdateAsync(config => config.PluginTrust[folderId] = entry, cancellationToken);

    internal static PluginTrustEntry? ReadArchiveEntry(JsonNode? node) =>
        node is JsonObject entry
        && entry["Source"] is JsonValue source && source.TryGetValue<string>(out var sourceKey) && !string.IsNullOrWhiteSpace(sourceKey)
        && entry["KeyFingerprint"] is JsonValue key && key.TryGetValue<string>(out var fingerprint) && !string.IsNullOrWhiteSpace(fingerprint)
            ? new PluginTrustEntry(sourceKey, fingerprint) : null;

    public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) =>
        _config.UpdateAsync(config => config.PluginTrust.Remove(folderId), cancellationToken);
}
