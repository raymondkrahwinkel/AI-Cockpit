namespace Cockpit.Core.Projects;

/// <summary>
/// One place outside <see cref="Project.SourceDirectory"/> that matters to a project's sessions (AC-483): a memory
/// folder, a Depot project, tomorrow an instruction file. <see cref="Project.Resources"/> is a list of these rather
/// than the single <see cref="Project.MemoryRef"/> a project used to carry, because a project can lean on more than
/// one memory source at once — a local folder <em>and</em> a Depot project together — and, once instruction files
/// exist, on rows of a different role entirely.
/// </summary>
/// <param name="Reference">
/// Where this resource is, or what names it — a folder path, or <c>&lt;scheme&gt;:&lt;value&gt;</c> naming a
/// plugin-contributed source. Free text for exactly the reason <see cref="Project.MemoryRef"/> already was one
/// (see its own doc comment, <c>Project.cs</c>): a plugin contributes kinds of reference this model cannot know
/// about in advance (a Depot project, AC-165/166), and those are not folders. The host stores what it is given and
/// says it plainly, same as it always has.
/// </param>
/// <param name="Role">
/// What a session does with this row — see <see cref="ProjectResourceRole"/>. Required rather than inferred from
/// the reference's shape: without it, every row would read as the same instruction to a session ("here is a
/// thing"), when a memory folder, an instruction file and a plain reference call for three different behaviors.
/// </param>
public sealed record ProjectResource(string Reference, ProjectResourceRole Role)
{
    /// <summary>What the operator calls this row, shown beside it wherever a project is edited. Null when they never named it — the bare reference is shown instead.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Whether a starting session should be told about this row. Stored and round-tripped now, but not yet honored:
    /// <c>Project.MemoryRef</c> is what a session is actually told about today, and it reads straight off
    /// <see cref="Project.Resources"/> without consulting this flag at all. AC-484 is the work that makes a session
    /// actually check it before speaking up.
    /// <para>
    /// Defaults to true — unlike <see cref="ProjectInfoField.IsSharedWithSessions"/>, which defaults off because an
    /// information row arrives as reference material for the operator first. A memory or instruction row exists
    /// specifically to be told to the session that will read or obey it, so the row that changes nothing is the one
    /// switched off, not the one switched on — once something reads this flag at all.
    /// </para>
    /// </summary>
    public bool ReachesSessions { get; init; } = true;
}
