using Cockpit.Core.Screenshots;

namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// Loads and persists <see cref="ScreenshotSettings"/> in <c>cockpit.json</c>. When nothing was ever saved,
/// <see cref="LoadAsync"/> returns the defaults (the global hotkey off).
/// </summary>
public interface IScreenshotSettingsStore
{
    Task<ScreenshotSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ScreenshotSettings settings, CancellationToken cancellationToken = default);
}
