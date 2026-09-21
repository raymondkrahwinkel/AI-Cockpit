using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

internal sealed class PluginSecretFieldStore : IPluginSecretFieldStore, ISingletonService, IAsyncDisposable
{
    private readonly CockpitConfigFileAccess _configFile;

    public PluginSecretFieldStore()
        : this(CockpitConfigPath.Default)
    {
    }

    internal PluginSecretFieldStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        return configFile?.PluginCredentialFields.Values.SelectMany(keys => keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
    }

    public Task DeclareAsync(string pluginId, IEnumerable<string> keys, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                var declared = file.PluginCredentialFields.TryGetValue(pluginId, out var existing)
                    ? new List<string>(existing)
                    : [];

                foreach (var key in keys.Where(key => !declared.Contains(key, StringComparer.OrdinalIgnoreCase)))
                {
                    declared.Add(key);
                }

                file.PluginCredentialFields[pluginId] = declared;
            },
            cancellationToken);

    // AC-1343: drains every UpdateAsync this instance fired as `_ = ...` (App.axaml.cs's plugin-storage
    // callback, which IPluginStorage.Set's void contract forces) before the container releases this singleton.
    public ValueTask DisposeAsync() => new(_configFile.FlushAsync());
}
