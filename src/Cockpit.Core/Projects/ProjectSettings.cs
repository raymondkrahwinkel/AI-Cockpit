namespace Cockpit.Core.Projects;

// The saved projects, under the `projects` section of `cockpit.json` (same store pattern as
// workspaces, layout and voice). Immutable; the `With…` helpers return a new instance and the store
// persists it.
public sealed record ProjectSettings
{
    // No projects — what an operator who never made one has, and what the cockpit behaves as today.
    public static ProjectSettings Empty { get; } = new();

    // The projects, in the order the manager and launcher show them.
    public IReadOnlyList<Project> Projects { get; init; } = [];

    // AC-1013: Ids of shared projects (AC-245) hidden on this machine only, never written into the shared
    // definition. Nothing in this build adds to this list yet (a later UI step); the read path already honours it.
    public IReadOnlyList<string> HiddenSharedProjectIds { get; init; } = [];

    // AC-1013: Categories in use (AC-618), in the order the manager shows their headings — operator-preserved
    // order, not alphabetical. Each entry is also the shown casing (first typed wins). Its own list rather than
    // derived from Projects on the fly, since order is a preference, not a project property; kept in sync by Normalized.
    public IReadOnlyList<string> CategoryOrder { get; init; } = [];

    // The project `projectId` names, or null — including for a session that belongs to a project the operator has since deleted.
    public Project? Find(string? projectId) =>
        string.IsNullOrEmpty(projectId) ? null : Projects.FirstOrDefault(project => project.Id == projectId);

    // Whether `sharedProjectId` is hidden on this machine (`HiddenSharedProjectIds`).
    public bool IsSharedProjectHidden(string sharedProjectId) =>
        HiddenSharedProjectIds.Contains(sharedProjectId, StringComparer.Ordinal);

    // AC-1013: These settings made safe to bind to (no missing id/name, no duplicate id, no blank info row).
    // Applied on load/before save so a hand-edited or half-written cockpit.json costs one entry, not the whole list.
    public ProjectSettings Normalized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Name))
            .Where(project => seen.Add(project.Id))
            .Select(_WithTidyInfo)
            .ToList();

        var result = usable.SequenceEqual(Projects) ? this : this with { Projects = usable };

        var hiddenSeen = new HashSet<string>(StringComparer.Ordinal);
        var hidden = new List<string>(result.HiddenSharedProjectIds.Count);
        foreach (var id in result.HiddenSharedProjectIds)
        {
            // A hand-edited cockpit.json can hold a JSON null here (System.Text.Json deserializes a `List<string>`
            // element of `null` as a null reference, not an empty string) — treated the same as a blank one, so
            // that one bad entry costs itself and not the whole list.
            var trimmed = id?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && hiddenSeen.Add(trimmed))
            {
                hidden.Add(trimmed);
            }
        }

        result = hidden.SequenceEqual(result.HiddenSharedProjectIds) ? result : result with { HiddenSharedProjectIds = hidden };

        var categoryOrder = _NormalizedCategoryOrder(result.CategoryOrder, result.Projects);
        return categoryOrder.SequenceEqual(result.CategoryOrder) ? result : result with { CategoryOrder = categoryOrder };
    }

    // AC-1013: `order` kept to exactly the categories `projects` still uses, keeping each entry's recorded
    // casing; a category not yet in `order` is appended using its first project's casing. Always OrdinalIgnoreCase
    // (never culture-sensitive) — see Project.Category's own remarks.
    private static IReadOnlyList<string> _NormalizedCategoryOrder(IReadOnlyList<string> order, IReadOnlyList<Project> projects)
    {
        var used = new List<string>();
        var usedSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            var category = project.Category?.Trim();
            if (!string.IsNullOrEmpty(category) && usedSeen.Add(category))
            {
                used.Add(category);
            }
        }

        var kept = new List<string>();
        var keptSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in order)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && usedSeen.Contains(trimmed) && keptSeen.Add(trimmed))
            {
                kept.Add(trimmed);
            }
        }

        foreach (var name in used)
        {
            if (keptSeen.Add(name))
            {
                kept.Add(name);
            }
        }

        return kept;
    }

    // AC-1013: `project` with info rows trimmed and blank ones dropped. Returns the *same instance* when
    // nothing changed — required because the caller's SequenceEqual on Project.AdditionalInfo is reference
    // equality for a list, not content; an unconditional `project with` would make the caller rebuild the whole list on every load.
    private static Project _WithTidyInfo(Project project)
    {
        var fields = project.AdditionalInfo
            .Select(field => field.Tidied())
            .Where(field => !field.IsBlank)
            .ToList();

        var tidied = fields.SequenceEqual(project.AdditionalInfo) ? project : project with { AdditionalInfo = fields };

        // AC-1013: Same reasoning as the info rows above — Normalized() runs before every save/load (ProjectStore),
        // so a cleared or half-written row costs one row, not a silently persisted reference to nothing.
        var resources = project.Resources.Where(resource => !string.IsNullOrWhiteSpace(resource.Reference)).ToList();
        tidied = resources.SequenceEqual(project.Resources) ? tidied : tidied with { Resources = resources };

        // A category typed and then deleted down to nothing, or a name that was only ever spaces, reads the same as
        // never having one — trimmed here so a whitespace-only Category can never itself hold a "used" category open
        // in _NormalizedCategoryOrder above.
        var category = string.IsNullOrWhiteSpace(project.Category) ? null : project.Category.Trim();
        tidied = category == project.Category ? tidied : tidied with { Category = category };

        return _TidyLinks(project.PluginFields) is { } links ? tidied with { PluginFields = links } : tidied;
    }

    // `links` trimmed and without entries that name nothing, or null when there was nothing to change — null
    // rather than an equal-content copy, since a record compares a dictionary by reference. Each value is
    // normalized item-by-item through `ProjectLinkValues` (AC-884): trimmed, deduplicated, rejoined.
    private static IReadOnlyDictionary<string, string>? _TidyLinks(IReadOnlyDictionary<string, string> links)
    {
        if (links.Count == 0)
        {
            return null;
        }

        var usable = new Dictionary<string, string>(links.Count, StringComparer.Ordinal);
        foreach (var (key, value) in links)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var tidied = ProjectLinkValues.Join(ProjectLinkValues.Split(value));
            if (tidied.Length > 0)
            {
                usable[key.Trim()] = tidied;
            }
        }

        var unchanged = usable.Count == links.Count
            && usable.All(link => links.TryGetValue(link.Key, out var original) && original == link.Value);

        return unchanged ? null : usable;
    }

    // These settings with `project` appended.
    public ProjectSettings WithProject(Project project) =>
        this with { Projects = [.. Projects, project] };

    // These settings with `projectId` removed (a no-op when it holds no such project).
    public ProjectSettings WithoutProject(string projectId) =>
        this with { Projects = [.. Projects.Where(project => project.Id != projectId)] };

    // These settings with `project` swapped in by id (a no-op when it holds no such project).
    public ProjectSettings WithUpdated(Project project) =>
        this with { Projects = [.. Projects.Select(existing => existing.Id == project.Id ? project : existing)] };
}
