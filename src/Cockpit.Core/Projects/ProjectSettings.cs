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

    // Ids of shared projects (`SharedProject.Id`, AC-245) hidden from the Projects workspace on this machine
    // — a per-machine visibility flag on a project that lives in a shared definition elsewhere, never written into
    // that definition itself, so hiding one here never hides it for a colleague. Nothing in this build ever adds to
    // this list yet (that UI is a deliberate later step); it exists so the read path already honours it once
    // something does.
    public IReadOnlyList<string> HiddenSharedProjectIds { get; init; } = [];

    // The categories in use (AC-618), in the order the manager shows their headings — not alphabetical, "Privé"
    // before "Werk" is a choice the operator gets to keep even though nothing yet lets them drag a heading. Each
    // entry is also the casing that category is shown under: the first project that typed it wins, and a later
    // project typing the same name differently (`StringComparison.OrdinalIgnoreCase`) still joins the same
    // group rather than starting a second one under its own casing — see `Project.Category`.
    //
    // Deliberately its own list rather than derived from `Projects` on the fly: a category is a
    // preference about the list, not a property of any one project, and deriving it fresh every time would have
    // no way to remember that "Privé" was typed before "Werk" once both have at least one project. Kept in sync
    // by `Normalized` — an entry drops out the moment no project carries its category any more (a
    // category "disappears when the last project lets go of it"), and a category typed onto a project the first
    // time is appended here, in the order its first project appears in `Projects`.
    public IReadOnlyList<string> CategoryOrder { get; init; } = [];

    // The project `projectId` names, or null — including for a session that belongs to a project the operator has since deleted.
    public Project? Find(string? projectId) =>
        string.IsNullOrEmpty(projectId) ? null : Projects.FirstOrDefault(project => project.Id == projectId);

    // Whether `sharedProjectId` is hidden on this machine (`HiddenSharedProjectIds`).
    public bool IsSharedProjectHidden(string sharedProjectId) =>
        HiddenSharedProjectIds.Contains(sharedProjectId, StringComparer.Ordinal);

    // These settings made safe to bind to: nothing without an id or a name, no id twice, and no blank information
    // row. Applied on load and before save, so a hand-edited or half-written `cockpit.json` costs the operator
    // an entry rather than the whole list. An entry missing either field cannot be shown or referenced, so keeping
    // it only means a blank row nothing can start.
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

    // `order` kept to exactly the categories `projects` still uses — each kept
    // entry keeps the casing already on record (that *is* the "shown as first typed" promise), and a
    // category some project carries that `order` has never recorded (newly typed, or a project
    // saved by a build from before this list existed) is appended in the order its first project appears, using
    // that project's own casing. Comparison throughout is `StringComparison.OrdinalIgnoreCase` — see
    // `Project.Category`'s own remarks on why the culture-sensitive default is never an option here.
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

    // `project` with its information rows trimmed onto one line and the empty ones gone — a row the
    // operator added and left alone carries nothing, and saving it would put a blank line on the project's card.
    //
    // Returning the *same instance* when there is nothing to tidy is what makes the caller's
    // `SequenceEqual` safe, and that is worth stating: a record's generated equality compares
    // `Project.AdditionalInfo` with the default comparer, which for a list is reference equality, not
    // content. Because this method only ever hands back either the original reference or a new project whose rows
    // genuinely differ (the inner `SequenceEqual` compares `ProjectInfoField` by value), there is no
    // third case where two references differ while the content matches. Simplify this to an unconditional
    // `project with` and the caller starts rebuilding the whole list on every load.
    private static Project _WithTidyInfo(Project project)
    {
        var fields = project.AdditionalInfo
            .Select(field => field.Tidied())
            .Where(field => !field.IsBlank)
            .ToList();

        var tidied = fields.SequenceEqual(project.AdditionalInfo) ? project : project with { AdditionalInfo = fields };

        // Same reasoning as the information rows above, and the same reason this drops a blank Reference here
        // rather than only in ProjectResourceEntry: Normalized() is what runs before every save and after every
        // load (ProjectStore), so a row an operator cleared or a hand edit left half-written costs the operator
        // that one row rather than silently persisting a reference nothing points at.
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
