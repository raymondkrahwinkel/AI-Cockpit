namespace Cockpit.Core.Projects;

/// <summary>
/// How far a <see cref="ProjectResource.Reference"/> travels (AC-605) — the question
/// <see cref="ProjectResourcePathPortability.ClassifyScope"/> answers, and what a resource row's editor badge shows
/// (AC-605 criterion 7). Mirrors the four shapes
/// <c>Cockpit.Plugin.Depot.ProjectDefinition.ProjectResourcePortability</c> classifies independently (that plugin
/// cannot reference this assembly — see that type's own remarks) — a shared naming convention would still be two
/// separate declarations, so the names here read as what each scope means for a session on some other machine, not
/// as a mirror of the plugin's own wire vocabulary.
/// </summary>
public enum ProjectResourceScope
{
    /// <summary>Relative to the project's own source folder — travels with the repo, e.g. <c>docs/CONVENTIONS.md</c>.</summary>
    Repo,

    /// <summary>
    /// Anchored to a home folder (<c>~</c> or <c>~/...</c>) — AC-605 criterion 3 makes this portable, so it travels
    /// in <c>.cockpit/project.json</c> to everyone the project is shared with, each resolved against whichever home
    /// directory the machine that opens it gives them. Named for the anchor, not for "stays with one person": that
    /// was the AC-482 framing this ticket reverses (Raymond, AC-605 review round) — <c>~/Notes/x</c> travels
    /// further than a repo-relative reference does, not less.
    /// </summary>
    Home,

    /// <summary>A plugin's own <c>&lt;scheme&gt;:&lt;value&gt;</c> reference — resolves through that plugin's own connection, the same for anyone with access to it.</summary>
    Instance,

    /// <summary>A fully qualified filesystem path — names a location only this one machine has.</summary>
    Machine,
}
