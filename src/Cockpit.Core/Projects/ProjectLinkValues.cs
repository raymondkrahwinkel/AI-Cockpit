namespace Cockpit.Core.Projects;

// One shared place to split/join/normalize a `Project.PluginFields` value that names more than one identifier
// (AC-884) — "EWB, AT, EJ, AUTH" for several YouTrack prefixes. Kept as `Dictionary<string,string>` rather than
// widening to `string[]`, so one item stays exactly today's value: no `cockpit.json` migration, no SDK break.
public static class ProjectLinkValues
{
    // The one separator every stored value normalizes to. A comma alone (no trailing space) still splits
    // correctly — Split trims each item — this is only what Join writes back.
    private const string Separator = ", ";

    // `raw` split on commas, each item trimmed, blank items dropped, and case-insensitively deduplicated
    // (first occurrence's casing wins) — empty for a null/blank/whitespace-only value.
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<string>();
        foreach (var part in raw.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                items.Add(trimmed);
            }
        }

        return items;
    }

    // `items` joined back into the one stored string, under the fixed separator every value normalizes to.
    public static string Join(IEnumerable<string> items) => string.Join(Separator, items);
}
