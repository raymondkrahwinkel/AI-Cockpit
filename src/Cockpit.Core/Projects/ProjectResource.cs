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
    /// Whether a starting session should be told about this row. Honored by
    /// <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> (AC-484): a row with this set to false is
    /// filtered out before any block of the standing instructions is built, so it never appears in a memory,
    /// instructions or reference sentence, whatever else the row says.
    /// <para>
    /// Defaults to true — unlike <see cref="ProjectInfoField.IsSharedWithSessions"/>, which defaults off because an
    /// information row arrives as reference material for the operator first. A memory or instruction row exists
    /// specifically to be told to the session that will read or obey it, so the row that changes nothing is the one
    /// switched off, not the one switched on — once something reads this flag at all.
    /// </para>
    /// </summary>
    public bool ReachesSessions { get; init; } = true;

    /// <summary>
    /// Whether this row's <em>contents</em> travel with the session rather than only its location (AC-486). Applies
    /// to <see cref="ProjectResourceRole.Instructions"/> alone: memory is read and written back to over the whole
    /// session and is far too large to carry, and a reference exists to be looked up when it is wanted.
    /// <para>
    /// Defaults to false, and that default is the guard rather than a preference. The alternative — inline whatever
    /// fits — would make the host decide whether a file is safe to hand over, and a rule that has to judge that will
    /// eventually judge it wrong, in the direction that leaks. As an opt-in there is nothing to judge: a file the
    /// operator did not tick is never opened, so "sensitive" is not a case to detect but a box left alone. (Raymond,
    /// 2026-07-29: <em>"niet inlinen wat gevoelig is"</em>.)
    /// </para>
    /// <para>
    /// Ticking it is a request, not a guarantee: the contents still have to fit what a project may contribute to a
    /// prompt, and a session is always told which of the two it got — the file itself, or only where to find it.
    /// Being told it holds instructions it was never given is worse than being told where they are.
    /// </para>
    /// </summary>
    public bool SendsContent { get; init; }
}
