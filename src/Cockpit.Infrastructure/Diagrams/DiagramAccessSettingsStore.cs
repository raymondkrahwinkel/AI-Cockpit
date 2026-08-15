using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Diagrams;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Diagrams;

// Persists the diagram-access master switch under the `diagramAccess` section of `cockpit.json` (AC-810), going
// through `CockpitConfigFileAccess` so it leaves every other section untouched. Mirrors TerminalAccessSettingsStore
// (AC-34).
internal sealed class DiagramAccessSettingsStore : IDiagramAccessSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public DiagramAccessSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal DiagramAccessSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<DiagramAccessSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.DiagramAccess?.ToDomain() ?? DiagramAccessSettings.Default;
    }

    public Task SaveAsync(DiagramAccessSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.DiagramAccess = DiagramAccessSettingsEntry.FromDomain(settings),
            cancellationToken);
}
