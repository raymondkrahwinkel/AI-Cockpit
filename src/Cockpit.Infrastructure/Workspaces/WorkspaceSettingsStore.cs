using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Workspaces;

/// <summary>
/// Persists <see cref="WorkspaceSettings"/> under the <c>workspaces</c> section of <c>cockpit.json</c> (same
/// file/pattern as <c>LayoutSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. When nothing was ever
/// saved, <see cref="LoadAsync"/> returns the default single Sessions workspace — an operator who never
/// touched workspaces gets the cockpit exactly as it behaves today.
/// </summary>
internal sealed class WorkspaceSettingsStore : IWorkspaceSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorkspaceSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
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
