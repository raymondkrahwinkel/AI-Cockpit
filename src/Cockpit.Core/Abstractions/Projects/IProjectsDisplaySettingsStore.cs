using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Projects;

/// <summary>
/// Loads and persists <see cref="ProjectsDisplaySettings"/> in <c>cockpit.json</c> — which layout the Projects page
/// draws (AC-772). When nothing was ever saved, <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface IProjectsDisplaySettingsStore
{
    Task<ProjectsDisplaySettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ProjectsDisplaySettings settings, CancellationToken cancellationToken = default);
}
