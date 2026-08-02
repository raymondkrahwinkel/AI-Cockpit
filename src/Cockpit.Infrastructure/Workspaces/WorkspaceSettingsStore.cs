using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Workspaces;

// Persists `WorkspaceSettings` under the `workspaces` section of `cockpit.json` (same
// file/pattern as `LayoutSettingsStore`). Reads-modifies-writes the whole file via
// `CockpitConfigFileAccess` so it leaves the other sections untouched. When nothing was ever
// saved, `LoadAsync` returns the default single Sessions workspace — an operator who never
// touched workspaces gets the cockpit exactly as it behaves today.
internal sealed class WorkspaceSettingsStore : IWorkspaceSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorkspaceSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal WorkspaceSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<WorkspaceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.Workspaces?.ToDomain() ?? WorkspaceSettings.Default;
    }

    public Task SaveAsync(WorkspaceSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            configFile => configFile.Workspaces = WorkspaceSettingsEntry.FromDomain(settings.Normalized()),
            cancellationToken);
}
