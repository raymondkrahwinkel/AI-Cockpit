using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Abstractions.Shortcuts;

/// <summary>
/// Loads and persists the app-action keyboard shortcuts (<see cref="ShortcutSettings"/>) in
/// <c>cockpit.json</c>. When nothing was ever saved, <see cref="LoadAsync"/> returns
/// <see cref="ShortcutSettings.Default"/>.
/// </summary>
public interface IShortcutSettingsStore
{
    Task<ShortcutSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ShortcutSettings settings, CancellationToken cancellationToken = default);
}
