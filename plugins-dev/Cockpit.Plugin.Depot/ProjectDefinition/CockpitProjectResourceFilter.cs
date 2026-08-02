namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>Splits candidate resource rows into what a written definition keeps and what it drops, with the reason (AC-244) — the reporting <see cref="CockpitProjectResourceEntry.Create"/> alone throws away by returning null.</summary>
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
                // A blank reference is not a path shape for Classify to judge (AC-244) — null here says "nothing to name", not "a specific unportable shape".
                var portability = string.IsNullOrWhiteSpace(reference) ? (ProjectResourcePortability?)null : ProjectResourcePortabilityClassifier.Classify(reference);
                dropped.Add(new CockpitProjectResourceDropped(role, reference, label, portability));
            }
        }

        return new CockpitProjectResourceFilterResult(portable, dropped);
    }
}
