using Cockpit.Core.Projects;

namespace Cockpit.Core.Abstractions.Projects;

/// <summary>Reads and writes the <c>projects</c> section of <c>cockpit.json</c> — same store pattern as workspaces, layout and voice.</summary>
public interface IProjectStore
{
    /// <summary>The saved projects, normalized (see <see cref="ProjectSettings.Normalized"/>); <see cref="ProjectSettings.Empty"/> when nothing is saved yet.</summary>
    Task<ProjectSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="settings"/>, leaving every other section of the config untouched.</summary>
    Task SaveAsync(ProjectSettings settings, CancellationToken cancellationToken = default);
}
