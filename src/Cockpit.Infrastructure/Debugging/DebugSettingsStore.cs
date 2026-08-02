using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Debugging;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Debugging;

// Persists `DebugSettings` under the `debug` section of `cockpit.json` (same
// file/pattern as `Layout.LayoutSettingsStore`). Reads-modifies-writes the whole file via
// `CockpitConfigFileAccess` so it leaves the other sections untouched.
internal sealed class DebugSettingsStore : IDebugSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DebugSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal DebugSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<DebugSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Debug?.ToDomain() ?? new DebugSettings();
    }

    public Task SaveAsync(DebugSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Debug = DebugSettingsEntry.FromDomain(settings),
            cancellationToken);
}
