namespace Cockpit.Core.Sessions;

/// <summary>
/// The thresholds an operator has set for themselves (AC-233), on top of what each provider declared. Two levels,
/// and the narrower one wins — the same precedence <c>SessionStartDefaults</c> uses, for the same reason: one rule,
/// applied in one place, rather than a copy per screen that can drift.
/// <para>
/// Absence means "follow the level above", never a zero. A field left alone keeps following the provider even
/// after the provider changes its mind, which is what an operator who never touched it would expect.
/// </para>
/// </summary>
public sealed class UsageThresholdSettings
{
    /// <summary>Per provider id, the signal keys that provider's sessions warn at differently from its own declaration.</summary>
    public Dictionary<string, Dictionary<string, double>> ByProvider { get; init; } = [];

    /// <summary>Per profile label, the signal keys that profile's sessions warn at differently again — for a profile used differently from the rest.</summary>
    public Dictionary<string, Dictionary<string, double>> ByProfile { get; init; } = [];

    /// <summary>
    /// Where <paramref name="signalKey"/> warns for a session under this provider and profile: the profile's answer
    /// if it gave one, else the provider's, else <paramref name="declared"/> — what the provider itself said.
    /// </summary>
    public double Resolve(string providerId, string? profileLabel, string signalKey, double declared)
    {
        if (profileLabel is { Length: > 0 }
            && ByProfile.TryGetValue(profileLabel, out var profile)
            && profile.TryGetValue(signalKey, out var fromProfile))
        {
            return fromProfile;
        }

        return ByProvider.TryGetValue(providerId, out var provider) && provider.TryGetValue(signalKey, out var fromProvider)
            ? fromProvider
            : declared;
    }

    /// <summary>
    /// Records an override, or clears it when <paramref name="percent"/> is null so the setting falls back to the
    /// level above rather than storing a copy of the current value.
    /// </summary>
    public void Set(Dictionary<string, Dictionary<string, double>> level, string owner, string signalKey, double? percent)
    {
        if (percent is not { } value)
        {
            if (level.TryGetValue(owner, out var existing))
            {
                existing.Remove(signalKey);
                if (existing.Count == 0)
                {
                    level.Remove(owner);
                }
            }

            return;
        }

        if (!level.TryGetValue(owner, out var signals))
        {
            signals = [];
            level[owner] = signals;
        }

        signals[signalKey] = Math.Clamp(value, 0, 100);
    }
}
