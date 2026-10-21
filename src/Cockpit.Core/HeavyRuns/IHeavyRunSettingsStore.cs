namespace Cockpit.Core.HeavyRuns;

/// <summary>
/// Persists <see cref="HeavyRunSettings"/> (AC-1535) under the <c>heavyRuns</c> section of <c>cockpit.json</c>.
/// </summary>
public interface IHeavyRunSettingsStore
{
    /// <summary>
    /// The stored settings, or the measured defaults when none were saved.
    /// </summary>
    Task<HeavyRunSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored settings, leaving the rest of the file untouched.
    /// </summary>
    Task SaveAsync(HeavyRunSettings settings, CancellationToken cancellationToken = default);
}
