namespace Cockpit.Core.Projects;

// AC-1013: How far a ProjectResource.Reference travels (AC-605) — what ClassifyScope answers and a resource
// row's editor badge shows. Mirrors, but does not share code with, the Depot plugin's own independent
// four-shape classifier (that plugin cannot reference this assembly); names read for a session's use, not the plugin's wire vocabulary.
public enum ProjectResourceScope
{
    // Relative to the project's own source folder — travels with the repo, e.g. `docs/CONVENTIONS.md`.
    Repo,

    // AC-1013: Anchored to a home folder (`~` or `~/...`) — AC-605 criterion 3 makes this portable: it travels
    // to everyone the project is shared with, resolved against each opener's own home. Named for the anchor,
    // reversing the AC-482 "stays with one person" framing (Raymond, AC-605 review): it travels further, not less.
    Home,

    // A plugin's own `&lt;scheme&gt;:&lt;value&gt;` reference — resolves through that plugin's own connection, the same for anyone with access to it.
    Instance,

    // A fully qualified filesystem path — names a location only this one machine has.
    Machine,
}
