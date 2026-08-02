using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// On-disk shape of one <c>resources[]</c> row in <c>.cockpit/project.json</c> (AC-244). <see cref="Role"/> and
/// <see cref="Portability"/> are plain strings, not enums — mirrors <c>ProjectResourceEntry.Role</c>'s own reasoning: a document-wide enum converter throws on an unrecognised value instead of failing just that row.
/// </summary>
public sealed class CockpitProjectResourceEntry
{
    public string Role { get; set; } = string.Empty;

    public string Reference { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>This row's <see cref="ProjectResourcePortability"/> wire value, as written by <see cref="Create"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Portability { get; set; }

    // AC-244: whatever a newer Cockpit wrote on this row that this build does not know about, carried through
    // untouched on a read-then-write.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Builds a row for writing, or null when <paramref name="reference"/> is blank, not portable, or (AC-612)
    /// recognised by <see cref="ProjectResourceSecretPathHeuristic"/> as likely credential material — Raymond's
    /// decision was that such a row never reaches a shared definition at all, the same treatment as an absolute
    /// path, checked here regardless of what shape the reference otherwise classifies as (an anchor-relative
    /// <c>~/.ssh/id_rsa</c> is portable by shape alone, which is exactly why this check cannot be folded into the
    /// portability gate above — it has to run whether or not that gate would have let the row through). A caller
    /// that needs to know *why* a row dropped (to tell the operator) wants <see cref="CockpitProjectResourceFilter.Apply"/>
    /// instead.
    /// </summary>
    public static CockpitProjectResourceEntry? Create(string role, string reference, string? label = null)
    {
        // A blank reference names nothing — not a path shape Classify should judge, a row with nothing to point at.
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var portability = ProjectResourcePortabilityClassifier.Classify(reference);
        if (!ProjectResourcePortabilityClassifier.IsPortable(portability) || ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference))
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
