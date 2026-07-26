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

    /// <summary>The project <paramref name="projectId"/> names, or null — including for a session that belongs to a project the operator has since deleted.</summary>
    public Project? Find(string? projectId) =>
        string.IsNullOrEmpty(projectId) ? null : Projects.FirstOrDefault(project => project.Id == projectId);

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

        return usable.SequenceEqual(Projects) ? this : this with { Projects = usable };
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
