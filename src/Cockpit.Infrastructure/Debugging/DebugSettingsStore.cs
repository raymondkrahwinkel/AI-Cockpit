using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Debugging;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Debugging;

/// <summary>
/// Persists <see cref="DebugSettings"/> under the <c>debug</c> section of <c>cockpit.json</c> (same
/// file/pattern as <see cref="Layout.LayoutSettingsStore"/>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched.
/// </summary>
internal sealed class DebugSettingsStore : IDebugSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DebugSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
