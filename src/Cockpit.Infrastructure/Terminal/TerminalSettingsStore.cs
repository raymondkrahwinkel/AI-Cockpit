using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Terminal;

// Persists `TerminalSettings` under the `terminal` section of `cockpit.json` (same pattern as `LayoutSettingsStore`).
// Reads-modifies-writes the whole file via `CockpitConfigFileAccess` so other sections stay untouched.
// When no settings were ever saved, `LoadAsync` returns the defaults.
internal sealed class TerminalSettingsStore : ITerminalSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TerminalSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal TerminalSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<TerminalSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Terminal?.ToDomain() ?? new TerminalSettings();
    }

    public Task SaveAsync(TerminalSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.Terminal = TerminalSettingsEntry.FromDomain(settings),
            cancellationToken);
}
