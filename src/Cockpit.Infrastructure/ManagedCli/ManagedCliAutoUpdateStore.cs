using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.ManagedCli;

// Persists the per-CLI auto-update exceptions (AC-767) under `managedCliAutoUpdateDisabled` in `cockpit.json`,
// going through `CockpitConfigFileAccess` so it leaves every other section untouched — same pattern as
// `TerminalAccessSettingsStore`. Stores the deviation, not the default: a CLI absent from the list is enabled.
internal sealed class ManagedCliAutoUpdateStore : IManagedCliAutoUpdateStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ManagedCliAutoUpdateStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ManagedCliAutoUpdateStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<bool> IsEnabledAsync(string cliName, CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return !(configFile?.ManagedCliAutoUpdateDisabled.Contains(cliName) ?? false);
    }

    public Task SetAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                if (enabled)
                {
                    file.ManagedCliAutoUpdateDisabled.Remove(cliName);
                }
                else if (!file.ManagedCliAutoUpdateDisabled.Contains(cliName))
                {
                    file.ManagedCliAutoUpdateDisabled.Add(cliName);
                }
            },
            cancellationToken);
}
