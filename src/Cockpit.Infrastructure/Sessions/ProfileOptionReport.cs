using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Reads a profile's stored option defaults against its provider's declared schema (AC-649), so a caller can be told
// what a profile is configured to run at instead of being handed the opaque map and left to guess (AC-647).
//
// *The provider's declaration is what is reported, never the stored map.* A key the profile stores that this provider
// does not declare is a leftover from another provider or an older build, and reading it out would be inventing
// meaning the host does not have. A provider that declares nothing therefore reports nothing — the honest answer for
// one whose settings live elsewhere entirely.
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
