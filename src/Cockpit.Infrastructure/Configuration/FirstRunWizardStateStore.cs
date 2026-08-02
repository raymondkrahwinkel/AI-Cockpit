using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Configuration;

// Persists `CockpitConfigFile.FirstRunWizardVersion` via the shared read-modify-write (AC-509), so it
// never clobbers a sibling section — the same pattern `PluginStoreConfigStore` uses for its own marker.
internal sealed class FirstRunWizardStateStore : IFirstRunWizardStateStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public FirstRunWizardStateStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal FirstRunWizardStateStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<int?> GetCompletedVersionAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.FirstRunWizardVersion;

    public Task MarkCompletedAsync(int version, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(file => file.FirstRunWizardVersion = version, cancellationToken);
}
