namespace Cockpit.Core.Projects;

/// <summary>
/// The saved projects, under the <c>projects</c> section of <c>cockpit.json</c> (same store pattern as
/// workspaces, layout and voice). Immutable; the <c>With…</c> helpers return a new instance and the store
/// persists it.
/// </summary>
public sealed record ProjectSettings
{
    /// <summary>No projects — what an operator who never made one has, and what the cockpit behaves as today.</summary>
    public static ProjectSettings Empty { get; } = new();

    /// <summary>The projects, in the order the manager and launcher show them.</summary>
    public IReadOnlyList<Project> Projects { get; init; } = [];

    /// <summary>
    /// Ids of shared projects (<c>SharedProject.Id</c>, AC-245) hidden from the Projects workspace on this machine
    /// — a per-machine visibility flag on a project that lives in a shared definition elsewhere, never written into
    /// that definition itself, so hiding one here never hides it for a colleague. Nothing in this build ever adds to
    /// this list yet (that UI is a deliberate later step); it exists so the read path already honours it once
    /// something does.
    /// </summary>
    public IReadOnlyList<string> HiddenSharedProjectIds { get; init; } = [];

    /// <summary>
    /// The categories in use (AC-618), in the order the manager shows their headings — not alphabetical, "Privé"
    /// before "Werk" is a choice the operator gets to keep even though nothing yet lets them drag a heading. Each
    /// entry is also the casing that category is shown under: the first project that typed it wins, and a later
    /// project typing the same name differently (<c>StringComparison.OrdinalIgnoreCase</c>) still joins the same
    /// group rather than starting a second one under its own casing — see <see cref="Project.Category"/>.
    /// <para>
    /// Deliberately its own list rather than derived from <see cref="Projects"/> on the fly: a category is a
    /// preference about the list, not a property of any one project, and deriving it fresh every time would have
    /// no way to remember that "Privé" was typed before "Werk" once both have at least one project. Kept in sync
    /// by <see cref="Normalized"/> — an entry drops out the moment no project carries its category any more (a
    /// category "disappears when the last project lets go of it"), and a category typed onto a project the first
    /// time is appended here, in the order its first project appears in <see cref="Projects"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> CategoryOrder { get; init; } = [];

    /// <summary>The project <paramref name="projectId"/> names, or null — including for a session that belongs to a project the operator has since deleted.</summary>
    public Project? Find(string? projectId) =>
        string.IsNullOrEmpty(projectId) ? null : Projects.FirstOrDefault(project => project.Id == projectId);

    /// <summary>Whether <paramref name="sharedProjectId"/> is hidden on this machine (<see cref="HiddenSharedProjectIds"/>).</summary>
    public bool IsSharedProjectHidden(string sharedProjectId) =>
        HiddenSharedProjectIds.Contains(sharedProjectId, StringComparer.Ordinal);

    /// <summary>
    /// These settings made safe to bind to: nothing without an id or a name, no id twice, and no blank information
    /// row. Applied on load and before save, so a hand-edited or half-written <c>cockpit.json</c> costs the operator
    /// an entry rather than the whole list. An entry missing either field cannot be shown or referenced, so keeping
    /// it only means a blank row nothing can start.
    /// </summary>
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

    /// <summary>
    /// <paramref name="order"/> kept to exactly the categories <paramref name="projects"/> still uses — each kept
    /// entry keeps the casing already on record (that <em>is</em> the "shown as first typed" promise), and a
    /// category some project carries that <paramref name="order"/> has never recorded (newly typed, or a project
    /// saved by a build from before this list existed) is appended in the order its first project appears, using
    /// that project's own casing. Comparison throughout is <see cref="StringComparison.OrdinalIgnoreCase"/> — see
    /// <see cref="Project.Category"/>'s own remarks on why the culture-sensitive default is never an option here.
    /// </summary>
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

    /// <summary>
    /// <paramref name="project"/> with its information rows trimmed onto one line and the empty ones gone — a row the
    /// operator added and left alone carries nothing, and saving it would put a blank line on the project's card.
    /// <para>
    /// Returning the <em>same instance</em> when there is nothing to tidy is what makes the caller's
    /// <c>SequenceEqual</c> safe, and that is worth stating: a record's generated equality compares
    /// <see cref="Project.AdditionalInfo"/> with the default comparer, which for a list is reference equality, not
    /// content. Because this method only ever hands back either the original reference or a new project whose rows
    /// genuinely differ (the inner <c>SequenceEqual</c> compares <see cref="ProjectInfoField"/> by value), there is no
    /// third case where two references differ while the content matches. Simplify this to an unconditional
    /// <c>project with</c> and the caller starts rebuilding the whole list on every load.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// <paramref name="links"/> trimmed and without the entries that name nothing, or null when there was nothing to
    /// change — null rather than the same content again for the reason <see cref="_WithTidyInfo"/> explains: a record
    /// compares a dictionary by reference, so handing back a fresh one that says the same thing would rebuild the whole
    /// project list on every load. A blank key or value is dropped: a plugin field the operator cleared is a link that
    /// is gone, and writing it as an empty string would leave a key nothing can be linked under.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? _TidyLinks(IReadOnlyDictionary<string, string> links)
    {
        if (links.Count == 0)
        {
            return null;
        }

        var usable = new Dictionary<string, string>(links.Count, StringComparer.Ordinal);
        foreach (var (key, value) in links)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                usable[key.Trim()] = value.Trim();
            }
        }

        var unchanged = usable.Count == links.Count
            && usable.All(link => links.TryGetValue(link.Key, out var original) && original == link.Value);

        return unchanged ? null : usable;
    }

    /// <summary>These settings with <paramref name="project"/> appended.</summary>
    public ProjectSettings WithProject(Project project) =>
        this with { Projects = [.. Projects, project] };

    /// <summary>These settings with <paramref name="projectId"/> removed (a no-op when it holds no such project).</summary>
    public ProjectSettings WithoutProject(string projectId) =>
        this with { Projects = [.. Projects.Where(project => project.Id != projectId)] };

    /// <summary>These settings with <paramref name="project"/> swapped in by id (a no-op when it holds no such project).</summary>
    public ProjectSettings WithUpdated(Project project) =>
        this with { Projects = [.. Projects.Select(existing => existing.Id == project.Id ? project : existing)] };
}
