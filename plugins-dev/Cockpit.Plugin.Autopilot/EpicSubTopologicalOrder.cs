namespace Cockpit.Plugin.Autopilot;

// Orders an epic's subs by their `depends on` links (AC-346), Kahn's algorithm: among candidates with no unmet
// dependency left, the lowest issue id is picked next — deterministic ordering (the ticket's "willekeurige
// maar deterministische volgorde"). A cyclic chain still produces an order: the lowest remaining id is taken anyway, breaking the cycle at the same point every time.
internal static class EpicSubTopologicalOrder
{
    // `issueIds`: Every sub in the epic, once each.
    // `dependsOn`: For a sub id, the sibling ids it depends on (a dependency outside `issueIds` is ignored — only order among the epic's own subs is this class's job).
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
