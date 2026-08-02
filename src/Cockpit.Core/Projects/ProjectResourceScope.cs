namespace Cockpit.Core.Projects;

// How far a `ProjectResource.Reference` travels (AC-605) — the question
// `ProjectResourcePathPortability.ClassifyScope` answers, and what a resource row's editor badge shows
// (AC-605 criterion 7). Mirrors the four shapes
// `Cockpit.Plugin.Depot.ProjectDefinition.ProjectResourcePortability` classifies independently (that plugin
// cannot reference this assembly — see that type's own remarks) — a shared naming convention would still be two
// separate declarations, so the names here read as what each scope means for a session on some other machine, not
// as a mirror of the plugin's own wire vocabulary.
public enum ProjectResourceScope
{
    // Relative to the project's own source folder — travels with the repo, e.g. `docs/CONVENTIONS.md`.
    Repo,

    // Anchored to a home folder (`~` or `~/...`) — AC-605 criterion 3 makes this portable, so it travels
    // in `.cockpit/project.json` to everyone the project is shared with, each resolved against whichever home
    // directory the machine that opens it gives them. Named for the anchor, not for "stays with one person": that
    // was the AC-482 framing this ticket reverses (Raymond, AC-605 review round) — `~/Notes/x` travels
    // further than a repo-relative reference does, not less.
    Home,

    // A plugin's own `&lt;scheme&gt;:&lt;value&gt;` reference — resolves through that plugin's own connection, the same for anyone with access to it.
    Instance,

    // A fully qualified filesystem path — names a location only this one machine has.
    Machine,
}
