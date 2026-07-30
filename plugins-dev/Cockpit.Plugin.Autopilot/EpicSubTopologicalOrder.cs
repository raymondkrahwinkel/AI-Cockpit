namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Orders an epic's subs by their <c>depends on</c> links (AC-346), Kahn's algorithm: a sub with no unmet dependency
/// left in the remaining set is a candidate, and among candidates the lowest issue id (ordinal, case-insensitive) is
/// picked next — a sub with no dependency on any sibling is exactly as free to run as one whose dependencies already
/// went, so ordering by id is what keeps the result stable and deterministic (the ticket's "willekeurige maar
/// deterministische volgorde") without the epic-runner having to invent a tie-break of its own.
/// <para>
/// A cyclic <c>depends on</c> chain — never written by Autopilot itself, but nothing stops a human from creating one in
/// the tracker — has no candidate with zero unmet dependencies once every acyclic sub is placed; rather than stall
/// forever, the lowest remaining id is taken anyway (breaking the cycle at the same deterministic point every time),
/// so a broken chain still produces an order instead of hanging the epic-runner.
/// </para>
/// </summary>
internal static class EpicSubTopologicalOrder
{
    /// <param name="issueIds">Every sub in the epic, once each.</param>
    /// <param name="dependsOn">For a sub id, the sibling ids it depends on (a dependency outside <paramref name="issueIds"/> is ignored — only order among the epic's own subs is this class's job).</param>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> issueIds, IReadOnlyDictionary<string, IReadOnlyList<string>> dependsOn)
    {
        var remaining = new HashSet<string>(issueIds, StringComparer.OrdinalIgnoreCase);
        var unmet = issueIds.ToDictionary(
            id => id,
            id => new HashSet<string>(
                (dependsOn.TryGetValue(id, out var deps) ? deps : []).Where(remaining.Contains),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        var order = new List<string>(issueIds.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => unmet[id].Count == 0).ToList();
            var next = (ready.Count > 0 ? (IEnumerable<string>)ready : remaining).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).First();

            order.Add(next);
            remaining.Remove(next);
            foreach (var id in remaining)
            {
                unmet[id].Remove(next);
            }
        }

        return order;
    }
}
