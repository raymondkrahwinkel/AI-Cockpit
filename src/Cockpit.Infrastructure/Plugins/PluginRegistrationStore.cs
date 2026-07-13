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
        _configFile.UpdateAsync(file =>
        {
            // Preserve the plugin's own stored data — this write owns only the enabled + hash state.
            var entry = file.Plugins.TryGetValue(folderId, out var existing) ? existing : new PluginRegistrationEntry();
            entry.Enabled = registration.Enabled;
            entry.PinnedSha256 = registration.PinnedSha256;
            file.Plugins[folderId] = entry;
        }, cancellationToken);

    public Task SaveMenuPreferenceAsync(string folderId, int menuOrder, bool hiddenInMenu, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file =>
        {
            // Mirror of SaveAsync: this write owns only the menu preference and leaves the enable/consent state
            // and the plugin's own data as they were.
            var entry = file.Plugins.TryGetValue(folderId, out var existing) ? existing : new PluginRegistrationEntry();
            entry.MenuOrder = menuOrder;
            entry.HiddenInMenu = hiddenInMenu;
            file.Plugins[folderId] = entry;
        }, cancellationToken);

    public Task RemoveAsync(string folderId, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => file.Plugins.Remove(folderId), cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> LoadDataAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Plugins.TryGetValue(folderId, out var entry) == true
            ? new Dictionary<string, string>(entry.Data)
            : new Dictionary<string, string>();
    }

    public Task SaveDataAsync(string folderId, IReadOnlyDictionary<string, string> data, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file =>
        {
            // Preserve the enabled + hash state — this write owns only the plugin's key/value data.
            var entry = file.Plugins.TryGetValue(folderId, out var existing) ? existing : new PluginRegistrationEntry();
            entry.Data = new Dictionary<string, string>(data);
            file.Plugins[folderId] = entry;
        }, cancellationToken);
}
