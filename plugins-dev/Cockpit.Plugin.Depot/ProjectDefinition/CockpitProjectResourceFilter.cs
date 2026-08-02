namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// Splits candidate resource rows into what a written definition keeps and what it drops, with the reason (AC-244)
/// — the reporting <see cref="CockpitProjectResourceEntry.Create"/> alone throws away by returning null.
/// <para>
/// AC-246: <see cref="CockpitProjectResourceFilterResult.Portable"/> is no longer only fully-portable rows despite
/// the name — a machine-scope row that is not secret-shaped now comes back as a <see cref="CockpitProjectResourceEntry.Placeholder"/>
/// entry in this same list rather than in <see cref="CockpitProjectResourceFilterResult.Dropped"/>: it does make it
/// into the written definition, role and label intact, just without a reference. <see cref="CockpitProjectResourceFilterResult.Dropped"/>
/// is left for what genuinely never reaches the definition at all any more: a blank reference, or one <see cref="ProjectResourceSecretPathHeuristic"/> recognises.
/// </para>
/// </summary>
public static class CockpitProjectResourceFilter
{
    public static CockpitProjectResourceFilterResult Apply(IEnumerable<(string Role, string Reference, string? Label)> rows)
    {
        var portable = new List<CockpitProjectResourceEntry>();
        var dropped = new List<CockpitProjectResourceDropped>();

        foreach (var (role, reference, label) in rows)
        {
            var entry = CockpitProjectResourceEntry.Create(role, reference, label);
            if (entry is not null)
            {
                portable.Add(entry);
            }
            else
            {
                // AC-246: Create returns null only for a blank reference or a secret-shaped one now — a plain
                // machine-scope row no longer lands here, it comes back as a Placeholder entry above instead.
                // A blank reference is not a path shape for Classify to judge (AC-244) — null here says "nothing to name", not "a specific unportable shape".
                var portability = string.IsNullOrWhiteSpace(reference) ? (ProjectResourcePortability?)null : ProjectResourcePortabilityClassifier.Classify(reference);
                dropped.Add(new CockpitProjectResourceDropped(role, reference, label, portability));
            }
        }

        return new CockpitProjectResourceFilterResult(portable, dropped);
    }
}
