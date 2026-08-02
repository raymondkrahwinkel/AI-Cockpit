namespace Cockpit.Plugin.Depot.ProjectDefinition;

// The four reference shapes AC-482 gives a project resource row (not yet landed as code as of AC-244, 2026-08-02 — reimplemented locally since a plugin project never references Cockpit.Core).
public enum ProjectResourcePortability
{
    // Relative to the project's own source folder — travels with the repo, e.g. `docs/CONVENTIONS.md`.
    RepoRelative,

    // Anchored to this operator's home folder, e.g. `~/Notes/...` — travels with the operator, not the repo.
    AnchorRelative,

    // A plugin's own `&lt;scheme&gt;:&lt;value&gt;` reference, e.g. `depot:slug/path` — resolves through that plugin's connection, not a disk.
    PluginSource,

    // A fully qualified filesystem path — names a location only this one machine has.
    Absolute,
}
