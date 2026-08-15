namespace Cockpit.Core.Sessions;

// The thresholds an operator has set for themselves (AC-233), on top of what each provider declared. Three
// levels, and the narrowest wins — the same precedence `SessionStartDefaults` uses, for the same reason: one
// rule, applied in one place, rather than a copy per screen that can drift.
//
// Absence means "follow the level above", never a zero. A field left alone keeps following the provider even
// after the provider changes its mind, which is what an operator who never touched it would expect.
public sealed class UsageThresholdSettings
{
    // Per provider id, the signal keys that provider's sessions warn at differently from its own declaration.
    public Dictionary<string, Dictionary<string, double>> ByProvider { get; init; } = [];

    // Per profile label, the signal keys that profile's sessions warn at differently again — for a profile used differently from the rest.
    public Dictionary<string, Dictionary<string, double>> ByProfile { get; init; } = [];

    // Per provider id, the signal keys the Assistant warns at differently again (AC-805). Keyed by role rather
    // than by profile label: the Assistant can run on the same profile as an ordinary session, and a profile
    // override cannot tell the two apart without also changing the ordinary session's own warning.
    public Dictionary<string, Dictionary<string, double>> ByAssistant { get; init; } = [];

    // Where `signalKey` warns for a session under this provider and profile: the Assistant's own answer if this
    // session is the Assistant and it gave one, else the profile's, else the provider's, else `declared` — what
    // the provider itself said.
    public double Resolve(string providerId, string? profileLabel, string signalKey, double declared, bool isAssistant)
    {
        if (isAssistant
            && ByAssistant.TryGetValue(providerId, out var assistant)
            && assistant.TryGetValue(signalKey, out var fromAssistant))
        {
            return fromAssistant;
        }

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

    // Records an override, or clears it when `percent` is null so the setting falls back to the
    // level above rather than storing a copy of the current value.
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
