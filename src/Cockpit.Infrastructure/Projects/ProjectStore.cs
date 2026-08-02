using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

// Persists `ProjectSettings` under the `projects` section of `cockpit.json` (same
// file/pattern as `WorkspaceSettingsStore`). Reads-modifies-writes the whole file via
// `CockpitConfigFileAccess` so it leaves the other sections untouched. Nothing saved yet means no
// projects — the cockpit then starts sessions exactly as it did before projects existed.
internal sealed class ProjectStore : IProjectStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ProjectStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ProjectStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ProjectSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null || (configFile.Projects.Count == 0 && configFile.HiddenSharedProjectIds.Count == 0 && configFile.CategoryOrder.Count == 0))
        {
            return ProjectSettings.Empty;
        }

        return new ProjectSettings
        {
            Projects = [.. configFile.Projects.Select(entry => entry.ToDomain())],
            HiddenSharedProjectIds = [.. configFile.HiddenSharedProjectIds],
            CategoryOrder = [.. configFile.CategoryOrder],
        }.Normalized();
    }

    public Task SaveAsync(ProjectSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            configFile =>
            {
                var normalized = settings.Normalized();
                configFile.Projects = [.. normalized.Projects.Select(ProjectEntry.FromDomain)];
                configFile.HiddenSharedProjectIds = [.. normalized.HiddenSharedProjectIds];
                configFile.CategoryOrder = [.. normalized.CategoryOrder];
            },
            cancellationToken);
}
