using Cockpit.Plugin.Depot.Secrets;

namespace Cockpit.Plugin.Depot.ProjectDefinition;

// Splits a project's IsSecret AdditionalInfo rows into what a written definition carries encrypted and what it
// drops, with the reason (AC-607) — mirrors CockpitProjectResourceFilter's reporting idiom (AC-244). Only rows
// where IsSecret is true are considered at all: a non-secret row is out of this ticket's scope entirely (AC-244's
// existing guard already keeps ALL AdditionalInfo off the wire; this only opens the door for the encrypted subset).
public static class CockpitProjectSensitiveFieldFilter
{
    private const string NoPasswordReason = "No project password is set for this project.";

    public static CockpitProjectSensitiveFieldFilterResult Apply(
        IEnumerable<(string Label, string Value, bool IsSecret)> rows, byte[]? dataKey)
    {
        var encrypted = new List<CockpitProjectSensitiveFieldEntry>();
        var dropped = new List<CockpitProjectSensitiveFieldDropped>();
        var protector = dataKey is null ? null : new ProjectSecretProtector(dataKey);

        foreach (var row in rows)
        {
            if (!row.IsSecret)
            {
                continue;
            }

            if (protector is null)
            {
                dropped.Add(new CockpitProjectSensitiveFieldDropped(row.Label, NoPasswordReason));
                continue;
            }

            // Known, accepted limitation: two rows sharing a label (ProjectInfoField's own doc-comment allows
            // this) collide on this AAD path, so "ciphertext cannot be swapped between fields" does not hold
            // between them — narrow blast radius, local trust boundary, not worth a redesign for this ticket.
            encrypted.Add(new CockpitProjectSensitiveFieldEntry
            {
                Label = row.Label,
                Value = protector.Protect($"SensitiveFields.{row.Label}", row.Value),
            });
        }

        return new CockpitProjectSensitiveFieldFilterResult(encrypted, dropped);
    }
}
