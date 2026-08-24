using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Reads a profile's stored option defaults against its provider's declared schema (AC-649/AC-647), so a caller
// gets a report instead of an opaque map. Only declared keys are reported — a stored key the provider doesn't
// declare may be a leftover from another provider or an older build, and reporting it would invent meaning.
public static class ProfileOptionReport
{
    // The declared options, each carrying what the profile sets it to or, failing that, the provider's own default.
    public static IReadOnlyList<AssistantProfileOptionRow> For(
        PluginSessionCapabilities? capabilities,
        IReadOnlyDictionary<string, string>? optionDefaults)
    {
        if (capabilities is null)
        {
            return [];
        }

        return
        [
            .. capabilities.DeclaredOptions.Select(option =>
            {
                var setOnProfile = optionDefaults?.TryGetValue(option.Key, out var stored) is true
                    && !string.IsNullOrWhiteSpace(stored);
                var value = setOnProfile ? optionDefaults![option.Key] : option.CurrentValueHint;

                return new AssistantProfileOptionRow(
                    option.Key,
                    option.Label,
                    value,
                    value is null ? null : _LabelFor(option, value),
                    setOnProfile);
            }),
        ];
    }

    // A value the provider does not list is still reported as itself: a free-form option (a model id) has no known
    // values at all, and a stale stored value is worth showing rather than blanking.
    private static string _LabelFor(PluginSessionOptionDescriptor option, string value) =>
        option.KnownValues?.FirstOrDefault(known => known.Value == value)?.Label ?? value;
}
