using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// What a programmatic spawn may change about a profile's start options, and what it may never (AC-648). The New-session
// dialog is where a human overrides these; a spawn has no dialog, so this is the same door with the checks written down.
//
// *Per key, never wholesale.* The result is the profile's own `OptionDefaults` with the named keys replaced — a caller
// that says `effort` and nothing else keeps every other value the profile was configured with, including the ones it
// may not name at all.
//
// *The provider's declaration decides what is nameable* (AC-649). A key this provider does not declare is refused with
// a reason rather than passed on: `effort` means nothing to Codex, and a spawn that accepted it would come up looking
// like it had worked.
public static class SpawnOptionOverrides
{
    // Refused whatever a provider declares, and refused outright rather than gated (Raymond, 2026-08-08): these decide
    // what a session may do to the machine, and a caller that could dial them up per spawn is the consent gate one hop
    // removed. `sandbox` is Codex's word for the same launch-time question Claude asks with `permission-mode`.
    public static readonly IReadOnlyList<string> NeverOverridable =
        [WellKnownPluginSessionOptions.PermissionMode, "sandbox"];

    // The options this spawn should launch with, or the reason it may not launch at all. A null map with no refusal
    // means nothing was asked for: the profile's own defaults stand untouched, which is every spawn made until now.
    public static (IReadOnlyDictionary<string, string>? Merged, string? Refusal) Merge(
        string providerName,
        PluginSessionCapabilities? capabilities,
        IReadOnlyDictionary<string, string>? optionDefaults,
        IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return (null, null);
        }

        var merged = optionDefaults?.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (rawKey, value) in overrides)
        {
            var key = rawKey.Trim();
            if (NeverOverridable.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                return (null, $"'{key}' is not something a spawn may set. It decides what the session is allowed to do, " +
                    "and it stays whatever the profile was configured with. If you need a session that runs differently, " +
                    "name a profile that already does.");
            }

            if (capabilities?.DeclaredOptions.FirstOrDefault(
                    option => string.Equals(option.Key, key, StringComparison.Ordinal)) is not { } declared)
            {
                var declaredKeys = capabilities?.DeclaredOptions ?? [];
                return (null, declaredKeys.Count == 0
                    ? $"{providerName} declares no options at all, so there is nothing to override — '{key}' included."
                    : $"{providerName} has no option called '{key}'. It takes: {string.Join(", ", declaredKeys.Select(option => $"'{option.Key}'"))}.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return (null, $"'{key}' was given no value. Leave it out to keep the profile's own.");
            }

            if (declared.KnownValues is { Count: > 0 } known
                && !known.Any(candidate => string.Equals(candidate.Value, value, StringComparison.Ordinal)))
            {
                return (null, $"'{value}' is not a value {providerName}'s '{key}' takes — it takes: " +
                    $"{string.Join(", ", known.Select(candidate => $"'{candidate.Value}'"))}.");
            }

            merged[key] = value;
        }

        return (merged, null);
    }
}
