namespace Cockpit.Core.Projects;

// One folder in Project.SourceDirectories. `Path` is the repository's folder, the same kind of value
// SourceDirectory has always held — item 0 of the list is that same folder, unchanged.
public sealed record ProjectRepository(string Path)
{
    // What the operator calls this repository ("web", "android"), so it can be told apart without reading the
    // whole path. Null when unnamed — readers then fall back to the folder's own name (Path.GetFileName).
    public string? Label { get; init; }
}
