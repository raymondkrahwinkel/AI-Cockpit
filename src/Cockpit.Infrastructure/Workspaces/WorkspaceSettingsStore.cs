using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Workspaces;

// Persists `WorkspaceSettings` under the `workspaces` section of `cockpit.json` (same file/pattern
// as `LayoutSettingsStore`), reading-modifying-writing the whole file so other sections stay untouched.
// When nothing was ever saved, `LoadAsync` returns the default single Sessions workspace, unchanged behavior.
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
