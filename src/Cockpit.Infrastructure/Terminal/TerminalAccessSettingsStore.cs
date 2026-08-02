using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Terminal;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Terminal;

// Persists the terminal-access master switch under the `terminalAccess` section of `cockpit.json` (AC-34),
// going through `CockpitConfigFileAccess` so it leaves every other section untouched — the same pattern
// as the worktree/layout settings stores.
internal sealed class TerminalAccessSettingsStore : ITerminalAccessSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public TerminalAccessSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal TerminalAccessSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<TerminalAccessSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.TerminalAccess?.ToDomain() ?? TerminalAccessSettings.Default;
    }

    public Task SaveAsync(TerminalAccessSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.TerminalAccess = TerminalAccessSettingsEntry.FromDomain(settings),
            cancellationToken);
}
