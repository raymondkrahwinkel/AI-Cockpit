using Cockpit.Core.Rendering;

namespace Cockpit.Core.Abstractions.Rendering;

/// <summary>
/// Loads and persists <see cref="RenderingSettings"/> in <c>cockpit.json</c> (the same config file the rest of
/// the settings live in). When nothing was ever saved, <see cref="LoadAsync"/> returns the defaults (Auto).
/// </summary>
public interface IRenderingSettingsStore
{
    Task<RenderingSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(RenderingSettings settings, CancellationToken cancellationToken = default);
}
