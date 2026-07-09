using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Plugins;

/// <summary>
/// Persists the <c>plugins</c> section of <c>cockpit.json</c> (#14) via the shared
/// <see cref="CockpitConfigFileAccess"/> read-modify-write, so it never clobbers a sibling section.
/// Registered as a singleton for the plugin manager; also instantiable directly (default ctor) for the
/// pre-container-build load pass in <c>Program.Main</c>.
/// </summary>
internal sealed class PluginRegistrationStore : IPluginRegistrationStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public PluginRegistrationStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal PluginRegistrationStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyDictionary<string, PluginRegistration>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, PluginRegistration>();
        if (configFile?.Plugins is { } plugins)
        {
            foreach (var (folderId, entry) in plugins)
            {
                result[folderId] = entry.ToDomain();
            }
        }

        return result;
    }

    public Task SaveAsync(string folderId, PluginRegistration registration, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => file.Plugins[folderId] = PluginRegistrationEntry.FromDomain(registration), cancellationToken);

    public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => file.Plugins.Remove(folderId), cancellationToken);
}
