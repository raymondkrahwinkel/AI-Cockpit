using Cockpit.Core.QuickNotes;

namespace Cockpit.Core.Abstractions.QuickNotes;

/// <summary>
/// Loads and persists <see cref="QuickNoteSettings"/> in <c>cockpit.json</c>. When nothing was ever saved,
/// <see cref="LoadAsync"/> returns the defaults (the global hotkey off).
/// </summary>
public interface IQuickNoteSettingsStore
{
    Task<QuickNoteSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(QuickNoteSettings settings, CancellationToken cancellationToken = default);
}
