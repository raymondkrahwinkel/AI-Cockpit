using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Abstractions.Worktrees;

/// <summary>
/// Loads and persists <see cref="WorktreeSettings"/> — the worktree-root override (AC-85) — in <c>cockpit.json</c>.
/// When nothing was ever saved, <see cref="LoadAsync"/> returns the defaults (no override, so the state-root default
/// is used).
/// </summary>
public interface IWorktreeSettingsStore
{
    /// <summary>The default worktree root used when no override is set — shown in Options so the operator sees what "blank" means.</summary>
    string DefaultRoot { get; }

    Task<WorktreeSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WorktreeSettings settings, CancellationToken cancellationToken = default);
}
