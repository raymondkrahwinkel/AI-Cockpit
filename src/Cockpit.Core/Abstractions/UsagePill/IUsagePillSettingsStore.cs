using Cockpit.Core.UsagePill;

namespace Cockpit.Core.Abstractions.UsagePill;

/// <summary>
/// Loads and persists <see cref="UsagePillSettings"/> in <c>cockpit.json</c> (the same config file the
/// profiles and transcript-display settings live in). When no settings were ever saved,
/// <see cref="LoadAsync"/> returns the defaults.
/// </summary>
public interface IUsagePillSettingsStore
{
    Task<UsagePillSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(UsagePillSettings settings, CancellationToken cancellationToken = default);
}
