using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

// Persists `ProjectsDisplaySettings` under the `projects` section of `cockpit.json` (same file/pattern as
// `LayoutSettingsStore`). Reads-modifies-writes the whole file via `CockpitConfigFileAccess` so it leaves the other
// sections untouched. When nothing was ever saved, `LoadAsync` returns the defaults.
internal sealed class ProjectsDisplaySettingsStore : IProjectsDisplaySettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ProjectsDisplaySettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal ProjectsDisplaySettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ProjectsDisplaySettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        // Normalized on the way out as well as in, so a layout a later build offered but this one does not never
        // reaches the page — the same defensive reason `LayoutSettingsStore` clamps on load and not only on save.
        return (configFile?.ProjectsDisplay?.ToDomain() ?? new ProjectsDisplaySettings()).Normalized();
    }

    public Task SaveAsync(ProjectsDisplaySettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.ProjectsDisplay = ProjectsDisplaySettingsEntry.FromDomain(settings.Normalized()),
            cancellationToken);
}
