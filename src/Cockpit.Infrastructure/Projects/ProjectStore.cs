using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Persists <see cref="ProjectSettings"/> under the <c>projects</c> section of <c>cockpit.json</c> (same
/// file/pattern as <c>WorkspaceSettingsStore</c>). Reads-modifies-writes the whole file via
/// <see cref="CockpitConfigFileAccess"/> so it leaves the other sections untouched. Nothing saved yet means no
/// projects — the cockpit then starts sessions exactly as it did before projects existed.
/// </summary>
internal sealed class ProjectStore : IProjectStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ProjectStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal ProjectStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ProjectSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null || configFile.Projects.Count == 0)
        {
            return ProjectSettings.Empty;
        }

        return new ProjectSettings { Projects = [.. configFile.Projects.Select(entry => entry.ToDomain())] }.Normalized();
    }

    public Task SaveAsync(ProjectSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            configFile => configFile.Projects = [.. settings.Normalized().Projects.Select(ProjectEntry.FromDomain)],
            cancellationToken);
}
