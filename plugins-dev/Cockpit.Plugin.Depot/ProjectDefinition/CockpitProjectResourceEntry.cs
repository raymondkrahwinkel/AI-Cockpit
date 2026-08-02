using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// On-disk shape of one <c>resources[]</c> row in <c>.cockpit/project.json</c> (AC-244). <see cref="Role"/> and
/// <see cref="Portability"/> are plain strings, not enums — mirrors <c>ProjectResourceEntry.Role</c>'s own reasoning: a document-wide enum converter throws on an unrecognised value instead of failing just that row.
/// <para>
/// AC-246 (Raymond, 2026-08-02): a machine-scope row is no longer an all-or-nothing drop. Two cases that used to
/// share one gate are now told apart — see <see cref="Create"/>'s own remarks.
/// </para>
/// </summary>
public sealed class CockpitProjectResourceEntry
{
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// Blank for a <see cref="Placeholder"/> row (AC-246) — the whole point of a placeholder is that this is
    /// exactly what does <em>not</em> travel. A reader must not treat a blank value here as "nothing to show": it
    /// is <see cref="Role"/>/<see cref="Label"/> that carry the row's meaning in that case, not this field.
    /// </summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>
    /// What the operator who wrote this row called it — and, since AC-246, the one piece of a placeholder row's
    /// own identity that <em>does</em> reach every colleague, even though <see cref="Reference"/> does not. Raymond
    /// accepted that price explicitly (2026-08-02): a label like <c>"Productie-DB"</c> already says plenty about
    /// what the row is for, which is exactly why a row <see cref="ProjectResourceSecretPathHeuristic"/> recognises
    /// as likely credential material skips this field too (see <see cref="Create"/>) rather than only the reference.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>This row's <see cref="ProjectResourcePortability"/> wire value, as written by <see cref="Create"/> — <c>"absolute"</c> for a <see cref="Placeholder"/> row too, since portability is still known even though the reference itself is withheld.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Portability { get; set; }

    /// <summary>
    /// True when this row exists only to say "a row belongs here, fill in your own path" (AC-246) — a machine-scope
    /// reference that is not secret-shaped. <see cref="Reference"/> is blank in that case; <see cref="Role"/> and
    /// <see cref="Label"/> still travel, so the operator who binds this project on a fresh machine knows what the
    /// row is for even though it names nothing yet.
    /// <para>
    /// Omitted from the wire (rather than written as <c>false</c>) for the overwhelmingly common row that is not
    /// one — <see cref="JsonIgnoreCondition.WhenWritingDefault"/> means an older reader that has never heard of this
    /// property sees nothing different about an ordinary row at all, only about the new machine-scope case.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Placeholder { get; set; }

    // AC-244: whatever a newer Cockpit wrote on this row that this build does not know about, carried through
    // untouched on a read-then-write. AC-246 measured this is exactly what shields an older build reading a
    // Placeholder row it does not recognise: the unknown "placeholder" key lands here, Reference simply
    // deserializes to its ordinary default ("") with no property missing and nothing to throw over — see
    // CockpitProjectDefinitionForwardCompatTests for the pinned proof (a real TryDeserialize/Serialize round trip,
    // not reasoned from this comment alone).
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// Builds a row for writing, or null when <paramref name="reference"/> is blank, or (AC-612) recognised by
    /// <see cref="ProjectResourceSecretPathHeuristic"/> as likely credential material.
    /// <para>
    /// AC-246 (Raymond, 2026-08-02) split what used to be one gate into two, because they answer different
    /// questions: a machine-scope reference (an absolute path) says <em>"this travels no further than the
    /// operator who wrote it"</em> — worth telling a colleague about, so <see cref="Role"/>/<see cref="Label"/>
    /// travel as a <see cref="Placeholder"/> row while <paramref name="reference"/> itself does not. A secret-shaped
    /// reference says <em>"this must never leave the machine in the clear"</em> — Raymond's decision there was
    /// explicit and unchanged: role, label and reference all stay home, the same full drop as before this ticket.
    /// The secret check still runs first and unconditionally (regardless of what shape the reference otherwise
    /// classifies as — an anchor-relative <c>~/.ssh/id_rsa</c> is portable by shape alone, which is exactly why this
    /// check cannot be folded into the portability branch below), so a secret-shaped absolute path is a full drop,
    /// never a placeholder that would leak its label. A caller that needs to know *why* a row dropped (to tell the
    /// operator) wants <see cref="CockpitProjectResourceFilter.Apply"/> instead.
    /// </para>
    /// </summary>
    public static CockpitProjectResourceEntry? Create(string role, string reference, string? label = null)
    {
        // A blank reference names nothing — not a path shape Classify should judge, a row with nothing to point at.
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        // AC-612, unconditional and first: never mind role/label, a secret-shaped reference is a full drop whatever
        // its portability shape says.
        if (ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference))
        {
            return null;
        }

        var portability = ProjectResourcePortabilityClassifier.Classify(reference);
        if (!ProjectResourcePortabilityClassifier.IsPortable(portability))
        {
            // AC-246: machine-scope, not secret — a placeholder, not a drop. The reference is this one machine's
            // own business; the fact that a row belongs here, and what it is for, is not.
            return new CockpitProjectResourceEntry
            {
                Role = role,
                Label = label,
                Portability = ProjectResourcePortabilityClassifier.ToWireValue(portability),
                Placeholder = true,
            };
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
