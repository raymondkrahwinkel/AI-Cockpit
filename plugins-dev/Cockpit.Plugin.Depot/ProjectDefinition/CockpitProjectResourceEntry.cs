using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// On-disk shape of one `resources[]` row in `.cockpit/project.json` (AC-244). `Role` and
// `Portability` are plain strings, not enums — mirrors `ProjectResourceEntry.Role`'s own reasoning: a document-wide enum converter throws on an unrecognised value instead of failing just that row.
public sealed class CockpitProjectResourceEntry
{
    public string Role { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    // This row's `ProjectResourcePortability` wire value, as written by `Create`.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Portability { get; set; }

    // AC-244: whatever a newer Cockpit wrote on this row that this build does not know about, carried through
    // untouched on a read-then-write.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // Builds a row for writing, or null when `reference` is blank or not portable — a caller that needs to know *why* a row dropped (to tell the operator) wants `CockpitProjectResourceFilter.Apply` instead.
    public static CockpitProjectResourceEntry? Create(string role, string reference, string? label = null)
    {
        // A blank reference names nothing — not a path shape Classify should judge, a row with nothing to point at.
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var portability = ProjectResourcePortabilityClassifier.Classify(reference);
        if (!ProjectResourcePortabilityClassifier.IsPortable(portability))
        {
            return null;
        }

        return new CockpitProjectResourceEntry
        {
            Role = role,
            Reference = reference,
            Label = label,
            Portability = ProjectResourcePortabilityClassifier.ToWireValue(portability),
        };
    }
}
