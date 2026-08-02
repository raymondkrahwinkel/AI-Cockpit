namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// The four reference shapes AC-482 gives a project resource row (not yet landed as code as of AC-244, 2026-08-02 — reimplemented locally since a plugin project never references Cockpit.Core).
/// </summary>
public enum ProjectResourcePortability
{
    /// <summary>Relative to the project's own source folder — travels with the repo, e.g. <c>docs/CONVENTIONS.md</c>.</summary>
    RepoRelative,

    /// <summary>Anchored to this operator's home folder, e.g. <c>~/Notes/...</c> — travels with the operator, not the repo.</summary>
    AnchorRelative,

    /// <summary>A plugin's own <c>&lt;scheme&gt;:&lt;value&gt;</c> reference, e.g. <c>depot:slug/path</c> — resolves through that plugin's connection, not a disk.</summary>
    PluginSource,

    /// <summary>A fully qualified filesystem path — names a location only this one machine has.</summary>
    Absolute,
}
