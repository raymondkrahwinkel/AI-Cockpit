namespace Cockpit.Core.Projects;

// One folder in Project.SourceDirectories (AC-938): a project used to carry exactly one repository as
// SourceDirectory, so a Waymark-shaped project (a web repo and an android repo, neither nested in the other) had
// nowhere to name its second one. Item 0 of the list is that same folder, unchanged; anything after it is an
// additional repository the project's sessions can be started in.
//
// `Path`: The repository's folder, the same kind of value SourceDirectory has always held.
public sealed record ProjectRepository(string Path)
{
    // What the operator calls this repository ("web", "android"), so an agent or the editor can tell two
    // repositories apart without reading the whole path (Raymond's addition to AC-938, 2026-08-18). Null when they
    // never named it — every reader falls back to the folder's own name (Path.GetFileName), the same "the bare
    // reference is shown instead" rule ProjectResource.Label already follows.
    public string? Label { get; init; }
}
