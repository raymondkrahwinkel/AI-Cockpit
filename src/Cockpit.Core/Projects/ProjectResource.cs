namespace Cockpit.Core.Projects;

// One place outside `Project.SourceDirectory` that matters to a project's sessions (AC-483): a memory
// folder, a Depot project, tomorrow an instruction file. `Project.Resources` is a list of these rather
// than the single `Project.MemoryRef` a project used to carry, because a project can lean on more than
// one memory source at once — a local folder *and* a Depot project together — and, once instruction files
// exist, on rows of a different role entirely.
//
// `Reference`:
// Where this resource is, or what names it — a folder path, or `&lt;scheme&gt;:&lt;value&gt;` naming a
// plugin-contributed source. Free text for exactly the reason `Project.MemoryRef` already was one
// (see its own doc comment, `Project.cs`): a plugin contributes kinds of reference this model cannot know
// about in advance (a Depot project, AC-165/166), and those are not folders. The host stores what it is given and
// says it plainly, same as it always has.
// `Role`:
// What a session does with this row — see `ProjectResourceRole`. Required rather than inferred from
// the reference's shape: without it, every row would read as the same instruction to a session ("here is a
// thing"), when a memory folder, an instruction file and a plain reference call for three different behaviors.
public sealed record ProjectResource(string Reference, ProjectResourceRole Role)
{
    // What the operator calls this row, shown beside it wherever a project is edited. Null when they never named it — the bare reference is shown instead.
    public string? Label { get; init; }

    // Whether a starting session should be told about this row. Honored by
    // `Cockpit.Core.Sessions.SessionStartDefaults.Resolve` (AC-484): a row with this set to false is
    // filtered out before any block of the standing instructions is built, so it never appears in a memory,
    // instructions or reference sentence, whatever else the row says.
    //
    // Defaults to true — unlike `ProjectInfoField.IsSharedWithSessions`, which defaults off because an
    // information row arrives as reference material for the operator first. A memory or instruction row exists
    // specifically to be told to the session that will read or obey it, so the row that changes nothing is the one
    // switched off, not the one switched on — once something reads this flag at all.
    public bool ReachesSessions { get; init; } = true;

    // Whether this row's *contents* travel with the session rather than only its location (AC-486). Applies
    // to `ProjectResourceRole.Instructions` alone: memory is read and written back to over the whole
    // session and is far too large to carry, and a reference exists to be looked up when it is wanted.
    //
    // Defaults to false, and that default is the guard rather than a preference. The alternative — inline whatever
    // fits — would make the host decide whether a file is safe to hand over, and a rule that has to judge that will
    // eventually judge it wrong, in the direction that leaks. As an opt-in there is nothing to judge: a file the
    // operator did not tick is never opened, so "sensitive" is not a case to detect but a box left alone. (Raymond,
    // 2026-07-29: *"niet inlinen wat gevoelig is"*.)
    //
    // Ticking it is a request, not a guarantee: the contents still have to fit what a project may contribute to a
    // prompt, and a session is always told which of the two it got — the file itself, or only where to find it.
    // Being told it holds instructions it was never given is worse than being told where they are.
    //
    // Reported as false for any role but `ProjectResourceRole.Instructions`, whatever was stored. The
    // flag and the role are two fields that can disagree, and every reader that only checked the flag would then be
    // a way in: a hand-edited `cockpit.json` can set it on a Memory row, where the editor shows no checkbox to
    // contradict it, and changing that row's role to Instructions afterwards used to carry the tick along —
    // arriving pre-ticked in front of an operator who never touched it, and opening the file from the next session
    // on. Enforced here rather than at each reader because "the operator ticked this" has to mean the same thing
    // everywhere it is asked, and there were already three places asking.
    //
    // AC-612: also reported as false when `Reference` looks like it names credential material (see
    // `ProjectResourceSecretPathHeuristic.IsLikelySecretPath`) — the same "enforced once, here, rather
    // than at every reader" reasoning as the role check above, and the one place Raymond's decision ("`SendsContent`
    // wordt geweigerd — de inhoud bereikt nooit een sessie-prompt") cannot be bypassed by a reader that skips
    // whatever check the editor or a plugin happens to run: a hand-edited `cockpit.json` naming
    // `~/.ssh/id_rsa` with this flag set gets the same false answer as one the editor built, without
    // `Infrastructure.Projects.ProjectInstructionContentReader` — or anything reading this property —
    // needing its own copy of the heuristic to stay honest.
    public bool SendsContent
    {
        get => _sendsContent && Role == ProjectResourceRole.Instructions && !ProjectResourceSecretPathHeuristic.IsLikelySecretPath(Reference);
        init => _sendsContent = value;
    }

    private readonly bool _sendsContent;
}
