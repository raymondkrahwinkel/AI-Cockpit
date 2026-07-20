using Cockpit.Core.Clones;

namespace Cockpit.Core.Abstractions.Clones;

/// <summary>
/// Loads and persists <see cref="CloneSettings"/> — the clones-root override (AC-90) — in <c>cockpit.json</c>. When
/// nothing was ever saved, <see cref="LoadAsync"/> returns the defaults (no override, so the state-root default is
/// used). Mirrors <c>IWorktreeSettingsStore</c> (AC-85): the same shape for the same kind of location setting.
/// </summary>
public interface ICloneSettingsStore
{
    /// <summary>The default clones root used when no override is set — shown in Options so the operator sees what "blank" means.</summary>
    string DefaultRoot { get; }

    Task<CloneSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(CloneSettings settings, CancellationToken cancellationToken = default);
}
