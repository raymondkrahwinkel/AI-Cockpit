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
    /// These settings made safe to bind to: nothing without an id or a name, and no id twice. Applied on load and
    /// before save, so a hand-edited or half-written <c>cockpit.json</c> costs the operator an entry rather than
    /// the whole list. An entry missing either field cannot be shown or referenced, so keeping it only means a
    /// blank row nothing can start.
    /// </summary>
    public ProjectSettings Normalized()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var usable = Projects
            .Where(project => !string.IsNullOrWhiteSpace(project.Id) && !string.IsNullOrWhiteSpace(project.Name))
            .Where(project => seen.Add(project.Id))
            .ToList();

        return usable.Count == Projects.Count ? this : this with { Projects = usable };
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
