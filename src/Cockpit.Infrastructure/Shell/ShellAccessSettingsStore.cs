using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Shell;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Shell;

// Persists the shell-access master switch under the `shellAccess` section of `cockpit.json` (AC-1066), going
// through `CockpitConfigFileAccess` so it leaves every other section untouched — the same pattern as
// TerminalAccessSettingsStore.
internal sealed class ShellAccessSettingsStore : IShellAccessSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ShellAccessSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ShellAccessSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ShellAccessSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.ShellAccess?.ToDomain() ?? ShellAccessSettings.Default;
    }

    public Task SaveAsync(ShellAccessSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.ShellAccess = ShellAccessSettingsEntry.FromDomain(settings),
            cancellationToken);
}
