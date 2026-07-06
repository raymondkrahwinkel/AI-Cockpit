using Cockpit.Core.Layout;

namespace Cockpit.Core.Abstractions.Layout;

/// <summary>
/// Loads and persists <see cref="LayoutSettings"/> in <c>cockpit.json</c> (the same config file the
/// profiles and notifications live in). When no settings were ever saved, <see cref="LoadAsync"/>
/// returns the defaults.
/// </summary>
public interface ILayoutSettingsStore
{
    Task<LayoutSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LayoutSettings settings, CancellationToken cancellationToken = default);
}
