using Cockpit.Core.Workspaces;

namespace Cockpit.Core.Abstractions.Workspaces;

/// <summary>Reads and writes the <c>workspaces</c> section of <c>cockpit.json</c> — same store pattern as layout, shortcuts and voice.</summary>
public interface IWorkspaceSettingsStore
{
    /// <summary>The saved workspaces, normalized (see <see cref="WorkspaceSettings.Normalized"/>); <see cref="WorkspaceSettings.Default"/> when nothing is saved yet.</summary>
    Task<WorkspaceSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists <paramref name="settings"/>, leaving every other section of the config untouched.</summary>
    Task SaveAsync(WorkspaceSettings settings, CancellationToken cancellationToken = default);
}
