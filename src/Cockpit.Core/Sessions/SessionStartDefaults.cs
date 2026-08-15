using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Sessions;

// What a new session opens with once a project and a profile have both had their say (AC-158/AC-159), and the
// only place the two meet. A project is an override on top of a profile: where both answer the same question
// the project wins, where it stays silent the profile's default stands, and where neither speaks the app
// default applies.
//
// One resolver rather than the rule repeated per caller, because the same question is asked from the New-session
// dialog, the launcher and the sidebar's quick-start. Three copies of a precedence rule are three chances for
// them to disagree, and then what a session starts with depends on which door it came through.
//
// `WorkingDirectory`: The folder to start in; null/blank leaves the caller on its own default.
// `IsolateInWorktree`: Whether to isolate in a git worktree (AC-85) when the folder is a repository. Still a per-session choice — this only pre-selects it.
// `ProfileLabel`: The profile to preselect, by label; null leaves the dialog's own selection alone.
// `EnabledMcpServerNames`:
// Which servers open ticked for a session started *without* a project — the profile's saved selection, or
// null for no restriction. A project answers this itself (`ProjectMcpOverlay.IsSelectedByDefault`) and
// its answer wins, so this is the fallback rather than the resolved value.
// `SystemPrompt`: The standing instructions to append to the provider's own system prompt: the profile's identity
// first (AC-142), then — under its own AC-714 attribution heading — the project's `BehaviorPrompt` and
// `Project.Resources` rows (instructions, memory, reference, then plain information). Null when none speak.
public sealed record SessionStartDefaults(
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? ProfileLabel,
    IReadOnlyList<string>? EnabledMcpServerNames,
    string? SystemPrompt)
{
    // The defaults for starting under `project` and `profile`, either of which
    // may be absent — a session without a project is how the cockpit has always started one.
    //
    // `globalWorkingDirectory`: The configured app-wide working directory, used when neither the project nor the profile names one.
    // `memorySources`:
    // The memory sources plugins have registered (AC-165/166, `ICockpitHost.AddProjectMemorySource`), so a
    // `ProjectResourceRole.Memory` row naming one of them is explained rather than merely quoted. Null
    // (the default) is exactly "none registered" — every caller that does not yet pass this list gets the plain,
    // unexplained sentence it always got, unchanged.
    // `unresolvedReferences`:
    // The `ProjectResource.Reference` values a caller has already checked and found missing (AC-484) —
    // deliberately an input, not something this method goes and finds out for itself. Resolving whether a
    // reference exists is I/O, and purity is a property this class keeps on purpose: the same rule is asked from
    // three different surfaces, and a resolver that sometimes touches disk and sometimes does not is one more way
    // for those three to disagree, this time depending on what the filesystem happened to look like at the moment
    // each was called. So a caller assembling an actual launch (`ProjectQuickStart`, the New-session dialog's
    // Start) runs its own small probe first and hands the result in as plain data; a caller only previewing a
    // field (a project or profile picker updating the working-directory box) can reasonably skip that and pass
    // null, which reads as "nothing known to be missing" rather than "nothing is missing" — the difference matters
    // only in that the former never mentions a broken reference, never that it blocks one.
    // `instructionContents`:
    // The file content already read (AC-486) for whichever `ProjectResourceRole.Instructions` rows
    // ticked `ProjectResource.SendsContent`, keyed by `ProjectResource.Reference` — the same
    // kind of input `unresolvedReferences` already is, and for the same reason: reading a file is
    // I/O, and this class stays pure whether the question is "does this exist" or "what does it say". A row ticked
    // for content with no entry here is read the same way either way this can happen — the reader could not read it
    // (missing, too large, a permissions error) or it simply was not asked for — so the session is told its
    // location only, with a note that the content it was ticked to carry did not come along; a session must never
    // be left thinking it saw a file's contents when it only ever got told where the file is. Null (the default) is
    // exactly "nothing was read" — every caller that does not yet pass this gets the location-only sentence this
    // class always produced, unchanged. The caller assembling an actual launch (`ProjectQuickStart`, the
    // New-session dialog's Start) runs `Cockpit.Infrastructure.Projects.ProjectInstructionContentReader.Read`
    // next to its own `ProjectResourceProbe` call and hands the result in here.
    // The MCP selection here stays the profile's, and that is not a gap in "the project wins": a project's
    // selection is a per-server answer rather than a list (`ProjectMcpOverlay.IsSelectedByDefault`),
    // applied where the checklist is built, and it beats this one wherever a project is in play. Resolving it into
    // a list here would need the catalog — which this rule deliberately knows nothing about.
    public static SessionStartDefaults Resolve(
        Project? project,
        SessionProfile? profile,
        string? globalWorkingDirectory = null,
        IReadOnlyList<ProjectMemorySource>? memorySources = null,
        IReadOnlyCollection<string>? unresolvedReferences = null,
        IReadOnlyDictionary<string, string>? instructionContents = null)
    {
        // A row switched off (ReachesSessions = false) is filtered out before any block below ever sees it — the
        // one place that rule is honored, rather than each block having to remember to check it itself. A blank
        // reference is filtered out here too (AC-484 review, MUST-FIX 6): reachable via {"role":"Memory"} without a
        // "reference" in a hand-edited cockpit.json (ProjectResourceEntry.ToDomain does Reference ?? string.Empty),
        // and every block below builds its sentence around this value — an empty one produces a "read the
        // instructions at " sentence pointing nowhere, in all three blocks alike. One filter here rather than three
        // repeats of the same guard.
        var reachable = project?.Resources
            .Where(resource => resource.ReachesSessions && !string.IsNullOrWhiteSpace(resource.Reference))
            .ToList()
            ?? (IReadOnlyList<ProjectResource>)[];
        var memoryRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Memory).ToList();
        var instructionRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Instructions).ToList();
        var referenceRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Reference).ToList();

        // Two different orders share this method, and confusing them is the bug an adversarial review found here
        // (AC-484 MUST-FIX 1): every block used to be built at full, unbounded length and only *then* measured — so
        // nothing ever stopped a single 20,000-character Label, or twenty 200-character paths, from blowing straight
        // through the shared ceiling before a single byte of it was ever compared against a budget.
        //
        // Assignment order (who gets a share of the ceiling first): memory, then instructions, then reference, then
        // the information rows. Memory goes first because the documented give-up order promises the memory sentence
        // survives every other cut — without it a session does not even know where to look for what this project
        // already knows, where a missing Instructions or Reference sentence merely costs convenience.
        //
        // Output order (what appears where in the joined prompt): instructions, then memory, then reference, then
        // information — unchanged from before, and independent of the line above. The most binding block is read
        // first regardless of which block happened to win the budget fight for its share of the ceiling.
        //
        // AC-714: the attribution heading is reserved out of this budget before any block below sees it, not added
        // on top of the ceiling once they are done — the ceiling must not tolerate a larger worst case than before.
        var remaining = Math.Max(0, ProjectContributionBudget - _ReservedLength(ProjectAttributionHeading));

        var memoryNote = _MemoryNote(memoryRows, memorySources, unresolvedReferences, remaining);
        remaining = Math.Max(0, remaining - _ReservedLength(memoryNote));

        // Give-up order, step 1 (within the instructions block itself, AC-486): for a fixed set of instruction
        // rows, whether to include their ticked file content at all is tried before a whole row is ever dropped —
        // see _InstructionsNote's own remarks.
        var instructionsNote = _InstructionsNote(instructionRows, instructionContents, unresolvedReferences, remaining);
        remaining = Math.Max(0, remaining - _ReservedLength(instructionsNote));

        var referenceNote = _ReferenceNote(referenceRows, unresolvedReferences, remaining);
        remaining = Math.Max(0, remaining - _ReservedLength(referenceNote));

        // The information rows are the one part of a project's contribution that was always built to shrink row by
        // row (see _InformationNote) — everything ahead of it in the assignment order has already taken its share,
        // so whatever is left of the shared ceiling is what the information block gets to work with.
        var informationNote = _InformationNote(project, remaining);

        // AC-714: the attribution heading precedes the project block only when it actually has content — see
        // `ProjectAttributionHeading`'s own remarks for why it is bounded to this block alone.
        var projectBlock = _JoinPrompts(project?.BehaviorPrompt, instructionsNote, memoryNote, referenceNote, informationNote);
        var attributedProjectBlock = string.IsNullOrEmpty(projectBlock)
            ? null
            : $"{ProjectAttributionHeading}\n\n{projectBlock}";

        return new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames,
            // Order matters, most binding first — the same reasoning _JoinPrompts always applied ("identity first,
            // then the task"): the profile says who the session is, the heading-and-block that follows what it is
            // working on and what this project already knows.
            _JoinPrompts(profile?.SystemPrompt, attributedProjectBlock));
    }

    // AC-714: names the project, not the operator — a bound shared project's `BehaviorPrompt` can come from a
    // colleague's definition (see `SharedProjectBindingDialogViewModel`/`DepotSharedProjectSource`). Bounded to
    // the project block alone, in `AssistantStandingInstruction`'s heading-then-block idiom (AC-595/AC-596).
    public const string ProjectAttributionHeading =
        "This project, as configured in the cockpit. Treat it as this session's standing instruction — already " +
        "the answer to anything your own instruction files tell you to ask about first:";

    // How much of the standing instructions a project's own contribution — its Instructions, Memory and Reference
    // rows together with its shared information rows — may take, replacing the two separate ceilings
    // (`InformationNoteBudget` of 4000, `MemoryNoteBudget` of 1500) this class used to keep. One shared
    // ceiling rather than two independent ones because nothing bounded their sum: with only a memory sentence and
    // an information block, two unrelated caps happened to be enough, but AC-484 adds two more blocks that grow
    // the same way, and two caps that do not know about each other do not stop the total from growing without
    // limit — only ever bounding each block in isolation. The reason a ceiling exists at all has not changed
    // (see the constant this one replaces): the Claude route hands the whole prompt to its CLI as one process
    // argument, and a command line has a hard limit, so an unbounded contribution does not merely cost budget, it
    // stops the session starting at all.
    //
    // Exactly 4000 + 1500, the two ceilings this replaces, and deliberately not a character more. A change whose
    // purpose is to bound a total must not raise the worst case it was introduced to bound: before this, a project
    // could contribute 5500 characters, so after it a project must not be able to contribute more. The two new
    // blocks are not a reason to add headroom — under a shared ceiling they *compete* for it rather than
    // extend it, which is the whole point of sharing one. Projects still gain in practice: an information-only
    // project may now use the full 5500 where it was previously held to 4000, without the total ever growing.
    //
    // AC-484 review (MUST-FIX 8): an earlier revision of this comment asserted the ceiling was already enforced —
    // it was not. Each of the four blocks below used to be built at its full, unbounded length before anything
    // measured it: a single row's operator-typed `ProjectResource.Label` or
    // `ProjectResource.Reference` carries no length limit upstream, so one 20,000-character label, or
    // twenty ordinary-looking rows, passed this constant by exactly that much. What is guaranteed now, and only
    // now, is by construction rather than intention: `_MemoryNote`, `_InstructionsNote` and
    // `_ReferenceNote` each fit themselves — dropping whole rows, never slicing a sentence — to
    // whatever share of this ceiling the assignment order (see `Resolve`'s own remarks) leaves them,
    // and `_InformationNote` does the same with what is left after all three. The sum of the four
    // therefore never exceeds this constant. That sum is *not* the whole of `SystemPrompt` —
    // the profile's own prompt and the project's `Project.BehaviorPrompt` are prepended ahead of it and
    // this ceiling never bounded either of those, before this fix or after.
    //
    // AC-484 confirming round (MUST-FIX 1): the claim above — "the sum of the four therefore never exceeds this
    // constant" — was itself not yet true when it was written. The one-row branch of `_MemoryNote` was
    // the single exception among the four: unlike the other three, it never measured its own output against the
    // budget it was handed at all, and the value it renders (a `Projects.ProjectMemorySource.Title`
    // folded into `_CappedSentence`'s `prefix`) was itself capped only in the part
    // `_CappedSentence` ever shortened — `value`, never `prefix` — so a `Title` alone
    // could still make the "capped" sentence run past `_PlaceNameBudget`, and past this constant with
    // it. Both gaps are closed now: `_CappedSentence` cuts `prefix` too when it alone leaves no
    // room for a value, and the one-row branch is measured against its budget exactly like the other three.
    private const int ProjectContributionBudget = 5500;

    // How long a single displayed value — a memory place name, an Instructions or Reference row's
    // `ProjectResource.Label` or bare `ProjectResource.Reference` — may run before it is
    // cut, kept separate from `ProjectContributionBudget` on purpose: that budget bounds how the
    // several blocks of a project's contribution share one ceiling, while this one bounds a single value's own
    // rendering regardless of how much of the shared ceiling happens to be free. Folding this into the shared
    // budget would let one absurdly long path or label alone consume nearly all of it, leaving nothing for the
    // other blocks even though the pathological value itself never needed six thousand characters — a bigger
    // shared ceiling should buy more *blocks* room, not license one value to grow into all of it. 1500 is the same
    // figure this class has always used here (see the git history this constant replaces): a realistic memory
    // location is a path or a project key, at most a few hundred characters, and a realistic source instruction is
    // a sentence or two a plugin author wrote by hand — 1500 leaves room for both together several times over
    // while still refusing to let either grow without limit.
    //
    // AC-484 review (MUST-FIX 2): originally applied only to the single-row memory sentence
    // (`_SingleMemorySentence`) via `_CappedSentence`. A second, uncapped route to the same
    // value — `_PlaceName`, used by the multi-row memory sentence — let a security guard a review had
    // put in place turn itself off the moment a project grew a second memory row: the same 6000-character value
    // that came out at 1500 characters as a single row came out whole, all 6000 of it, as one of two. Every
    // place-name-shaped value now goes through `_CappedSentence` against this same constant — in the
    // memory block (`_PlaceName`) and in the Instructions/Reference blocks
    // (`_ResourceDisplay`) alike — so there is exactly one route to "how long may a value run", not one
    // per block.
    private const int _PlaceNameBudget = 1500;

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    // An estimate of how much of the shared budget a note actually costs once joined: its own length plus the blank line `_JoinPrompts` puts before the next part. Null/blank costs nothing — it does not appear in the joined prompt at all.
    private static int _ReservedLength(string? note) => string.IsNullOrEmpty(note) ? 0 : note.Length + 2;

    // Whether `reference` is one a caller's probe already found missing — see `Resolve`'s remarks on `unresolvedReferences` for why this class never checks that itself.
    private static bool _IsUnresolved(string reference, IReadOnlyCollection<string>? unresolvedReferences) =>
        unresolvedReferences is { Count: > 0 } && unresolvedReferences.Contains(reference);

    // `missingPlaceNames` said out loud rather than silently — the same courtesy
    // `_InformationNote` gives a row that did not fit, applied here to a reference that could not be
    // found at all. Never blocks anything it is attached to: an agent that thinks a place holds no conventions
    // behaves differently from one that knows it simply could not read them, so the gap is named, and the session
    // starts regardless — the same line the bundled-plugin installer draws ("a convenience, not a dependency").
    private static string _NotFoundSuffix(IReadOnlyList<string> missingPlaceNames) => missingPlaceNames.Count switch
    {
        0 => string.Empty,
        1 => $" {missingPlaceNames[0]} could not be found there — the session starts anyway; check the location when you get a chance.",
        _ => $" The following could not be found: {_JoinWithAnd(missingPlaceNames)} — the session starts anyway; check them when you get a chance.",
    };

    // `items` joined as an English list: empty for none, the one item alone, "a and b" for two, "a, b and c" for more.
    private static string _JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    // Step 1 of the give-up order documented on `ProjectContributionBudget`, now that AC-486 gives this
    // block content to carry in the first place: the same rendering `_InstructionsNoteText` produces
    // for `kept`, but with every ticked row's content held back — content is the least essential
    // part of an instructions block (the session can still be told where to go read it itself), where the memory
    // sentence is the one thing a session cannot do without at all. Called by `_InstructionsNote` only
    // once the same row count's content-carrying rendering has already been tried and did not fit — a whole row is
    // never dropped to make room for a row's content when dropping the content alone would have been enough.
    private static string _WithoutInstructionContent(
        IReadOnlyList<ProjectResource> kept, IReadOnlyCollection<string>? unresolvedReferences, int dropped) =>
        _InstructionsNoteText(kept, instructionContents: null, unresolvedReferences, dropped, includeContent: false);

    // Sentence endings that count as "already punctuated" for `_SingleMemorySentence` — the same idea
    // `_InformationNote` applies to a label's trailing colon, just for a sentence rather than a word.
    private static readonly char[] _SentenceEndings = ['.', '!', '?'];

    // How a single `ProjectResourceRole.Memory` row's `ProjectResource.Reference` is said
    // back to the session, for whichever `memorySources` row (if any) explains it — named via
    // that source's own `Title`, without its `Instruction`. Used both to render the single-row memory
    // sentence (`_SingleMemorySentence`) and to name a row in a multi-row sentence or a not-found
    // mention, so the two say the same place the same way.
    //
    // AC-484 review (MUST-FIX 2): every branch below is now put through `_CappedSentence` against
    // `_PlaceNameBudget`, the same cap `_SingleMemorySentence` already applied to the
    // single-row case. Before this fix this method returned `reference`'s value uncapped, so the
    // single-row route and the multi-row route disagreed on a value of the same length: 1500 characters
    // (truncated) as one row, the whole uncapped value as one of two. Two routes to the same rendering is exactly
    // the shape in which a guard a security review put in place quietly stops applying — the fix is one route.
    private static string _PlaceName(string reference, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        var trimmed = ProjectPromptText.OneLine(reference.Trim());
        if (memorySources is { Count: > 0 }
            && ProjectMemoryRef.TryParse(trimmed, out var scheme, out var value)
            && memorySources.FirstOrDefault(source => string.Equals(source.Scheme, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched
            && ProjectPromptText.OneLine(matched.Title.Trim()) is { Length: > 0 } title)
        {
            return _CappedSentence($"{title} \"", ProjectPromptText.OneLine(value), "\"");
        }

        return _CappedSentence(string.Empty, trimmed, string.Empty);
    }

    // The registered `memorySources` entry `reference` actually names, or null
    // when it does not name one at all — a value with no `&lt;scheme&gt;:&lt;value&gt;` shape at all
    // (`ProjectMemoryRef.TryParse` fails), or one whose scheme nothing registered claims.
    //
    // AC-484 confirming round (FIX 2): the one place this match is made, now shared by the multi-row channel-advice
    // check and the multi-row instruction text (`_MultiRowInstructionsText`) as well as by
    // `_PlaceName` and `_SingleMemorySentence`'s own inline version of the same test.
    // Before this helper existed, the channel-advice check asked only whether a reference had the
    // *shape* `&lt;scheme&gt;:&lt;value&gt;` — which a plain URL kept as a second memory row
    // ("https://intranet.example/wiki") satisfies without there being any registered source behind it at all — so
    // a project with one local folder and one URL got advice naming an MCP or remote source that was never
    // actually in play. "Looks like a reference" and "names a source this session can actually be pointed at" are
    // different questions, and only the second one should ever change what the session is told to do.
    private static bool _NamesRegisteredSource(string reference, IReadOnlyList<ProjectMemorySource>? memorySources) =>
        _MatchedMemorySource(reference, memorySources) is not null;

    // What an Instructions or Reference row is called back to the session: the operator's own
    // `ProjectResource.Label` when they gave one, the bare reference otherwise — the same choice
    // `Projects.ProjectInfoField` makes for an unlabelled row. Capped through
    // `_CappedSentence` against `_PlaceNameBudget` (AC-484 review, MUST-FIX 1/2): a
    // `ProjectResource.Label` is operator-free text with no length limit upstream, and was the
    // vector an adversarial review used to blow straight through the shared ceiling with a single 20,000-character
    // row.
    private static string _ResourceDisplay(ProjectResource row)
    {
        var raw = !string.IsNullOrWhiteSpace(row.Label) ? row.Label : row.Reference;
        return _CappedSentence(string.Empty, ProjectPromptText.OneLine(raw.Trim()), string.Empty);
    }

    // Where a project's memory lives, said in a sentence the session can act on. Null when it keeps none, or when
    // nothing about it fits within `budget` — this block goes first in the assignment order (see
    // `Resolve`'s own remarks), so that second case is reachable only under an absurdly small ceiling.
    //
    // One row says it exactly the way this class always has (`_SingleMemorySentence`) — that
    // byte-for-byte match is deliberate: a project with a single memory row is by far the common case, and every
    // caller and every test written against the old single-`MemoryRef` world keeps working unchanged.
    //
    // More than one row says so in a single sentence naming all of them that fit (AC-484 review, MUST-FIX 1: rows
    // that do not fit are dropped from the end, never a sentence sliced mid-way, and the drop is announced —
    // see `_FitRowsToBudget`). Only when the surviving rows are genuinely of both kinds — at least one
    // reference naming a *registered* `memorySources` entry and at least one that does not
    // (`_NamesRegisteredSource` tells them apart; AC-484 confirming round, FIX 2 — a bare
    // `&lt;scheme&gt;:&lt;value&gt;` *shape* is not enough, a plain URL kept as a memory row has that
    // shape too without any source standing behind it) — does a second sentence follow on which channel to use for
    // what: the local folder for searching, bulk reading and working offline, the MCP or remote source for the
    // current shared state (AC-484 review, MUST-FIX 5). Two local folders have no MCP to send an agent to, and two
    // `depot:` rows have no "local folder" for "the local folder" to name — advice naming a channel that is
    // not actually in play is worse than no advice, so it is withheld unless both channels are genuinely present.
    //
    // The multi-row sentence also says how to reach what it names (AC-484 confirming round, FIX 3): a distinct
    // registered source's own `Instruction` once per source, and the same "read it there and keep it up to
    // date" gist `_SingleMemorySentence` already falls back to for a single unregistered row, once for
    // however many unregistered rows survived the cut — see `_MultiRowInstructionsText`. Before this
    // fix the multi-row branch named the places and stopped, which is exactly the outcome the registry's own
    // blank-instruction refusal exists to rule out ("naming a place a session cannot be told how to reach leaves
    // it no better off than the bare reference"). Added only when the whole of it still fits the budget, and left
    // out whole rather than sliced when it does not — the same rule `_FitRowsToBudget` already applies
    // to a row that does not fit on its own.
    private static string? _MemoryNote(
        IReadOnlyList<ProjectResource> memoryRows,
        IReadOnlyList<ProjectMemorySource>? memorySources,
        IReadOnlyCollection<string>? unresolvedReferences,
        int budget)
    {
        if (memoryRows.Count == 0)
        {
            return null;
        }

        if (memoryRows.Count == 1)
        {
            var reference = memoryRows[0].Reference;
            var sentence = _SingleMemorySentence(reference, memorySources);
            if (sentence is null)
            {
                return null;
            }

            var missing = _IsUnresolved(reference, unresolvedReferences)
                ? new[] { _PlaceName(reference, memorySources) }
                : [];
            var withSuffix = sentence + _NotFoundSuffix(missing);

            // AC-484 confirming round (MUST-FIX 1): the one-row branch used to hand this back unmeasured — the
            // only one of the four blocks that never checked its own output against `budget` at all, so nothing
            // downstream ever caught a one-row sentence that still ran long (an absurd Title, capped per value but
            // combined with its own not-found mention, could still add up to more than this call was ever given to
            // spend). Dropped rather than truncated, the same rule _FitRowsToBudget already applies to a row that
            // does not fit on its own: a sentence sliced mid-way can flip what it says.
            return withSuffix.Length <= budget ? withSuffix : null;
        }

        var effectiveBudget = Math.Max(0, budget);
        return _FitRowsToBudget(memoryRows, effectiveBudget, (kept, dropped) =>
        {
            var places = kept.Select(row => _PlaceName(row.Reference, memorySources)).ToList();
            var missingPlaces = kept
                .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
                .Select(row => _PlaceName(row.Reference, memorySources))
                .ToList();

            // Both channels have to be genuinely present among the rows that survived the cut — a row dropped for
            // budget is not "in play" any more than one never entered, so this is judged on `kept`, not on
            // `memoryRows` as a whole. "Present" means a row actually names a *registered* source (AC-484
            // confirming round, FIX 2), not merely a value shaped like one — see _NamesRegisteredSource's own
            // remarks on why the shape alone (ProjectMemoryRef.TryParse) let a plain URL trigger this advice.
            var hasSchemeRow = kept.Any(row => _NamesRegisteredSource(row.Reference, memorySources));
            var hasPathRow = kept.Any(row => !_NamesRegisteredSource(row.Reference, memorySources));
            var channelAdvice = hasSchemeRow && hasPathRow
                ? " Use the local folder to search it, read it in bulk and work offline; use the MCP or remote " +
                  "source for the current shared state. Reading may draw from either channel, but prefer writing " +
                  "any one file through a single channel within a session — write the same file through both and " +
                  "the next sync collides with your own edit."
                : string.Empty;

            var baseSentence = $"This project's memory lives in {_JoinWithAnd(places)}.";
            var tail = channelAdvice + _NotFoundSuffix(missingPlaces) + _DroppedRowsSuffix(dropped);

            // AC-484 confirming round (FIX 3): how to reach it is said here too, right after the sentence naming
            // where it is — added only when the whole addition still fits `effectiveBudget`, and left out whole
            // rather than sliced when it does not, exactly the way any other row _FitRowsToBudget considers either
            // survives whole or is dropped, never cut mid-sentence.
            var instructionsText = _MultiRowInstructionsText(kept, memorySources);
            if (instructionsText.Length > 0)
            {
                var withInstructions = $"{baseSentence} {instructionsText}{tail}";
                if (withInstructions.Length <= effectiveBudget)
                {
                    return withInstructions;
                }
            }

            return $"{baseSentence}{tail}";
        });
    }

    // The multi-row memory sentence's "how to reach it" text (AC-484 confirming round, FIX 3), assembled the same
    // way `_SingleMemorySentence` already says it for a single row: a distinct registered source's own
    // `Instruction`, tidied to one line and left with its own trailing punctuation if it already had one —
    // once per distinct source, not once per row, so two rows naming the same source (two `depot:` paths, for
    // instance) do not repeat its instruction. Any row among `kept` that does not name a
    // registered source at all is covered once, not once per such row, by the same generic "read it there and keep
    // it up to date" gist the single-row sentence falls back to for an unregistered reference — a session with two
    // plain folders still needs telling how to treat them, just not a different sentence for each.
    //
    // Empty when nothing here has anything to add — every kept row names a registered source whose
    // `Instruction` is itself blank (the registry refuses that, so this is reachable only for a caller that
    // built its own `memorySources` list, the same escape hatch `_SingleMemorySentence`
    // already allows).
    private static string _MultiRowInstructionsText(IReadOnlyList<ProjectResource> kept, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        var sentences = new List<string>();
        var coveredSources = new HashSet<ProjectMemorySource>();
        var hasUnregisteredRow = false;

        foreach (var row in kept)
        {
            var matched = _MatchedMemorySource(row.Reference, memorySources);
            if (matched is null)
            {
                hasUnregisteredRow = true;
                continue;
            }

            if (!coveredSources.Add(matched))
            {
                // Already covered by an earlier row naming the very same source.
                continue;
            }

            var instruction = ProjectPromptText.OneLine(matched.Instruction.Trim());
            if (instruction.Length > 0)
            {
                sentences.Add(_SentenceEndings.Contains(instruction[^1]) ? instruction : $"{instruction}.");
            }
        }

        if (hasUnregisteredRow)
        {
            sentences.Add("Read it there when you need what this project already knows, and keep it up to date as you work.");
        }

        return string.Join(" ", sentences);
    }

    // The registered `memorySources` entry `reference` names, or null when it
    // does not name one at all — shared by `_NamesRegisteredSource` and
    // `_MultiRowInstructionsText` so both ask the identical question `_PlaceName` and
    // `_SingleMemorySentence` already ask inline for their own single-value cases.
    private static ProjectMemorySource? _MatchedMemorySource(string reference, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        if (memorySources is not { Count: > 0 })
        {
            return null;
        }

        var trimmed = ProjectPromptText.OneLine(reference.Trim());
        return ProjectMemoryRef.TryParse(trimmed, out var scheme, out _)
            ? memorySources.FirstOrDefault(source => string.Equals(source.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    // The single-row memory sentence, unchanged from the code AC-484 found here (only the input shape changed,
    // from `Project.MemoryRef` straight to a resource's `ProjectResource.Reference` — the two are
    // the same string for the row this is called with).
    //
    // A reference of the shape `&lt;scheme&gt;:&lt;value&gt;` naming a registered `memorySources`
    // entry is explained — named by that source's own `Title`, with its `Instruction` appended so the
    // session is told how to reach it, not only where it is. Anything else — a bare path, a scheme nothing
    // registered, an empty value after the colon, a matched source whose `Title` is itself blank — falls back
    // to the plain, unexplained sentence this always said: deliberately told rather than loaded, because the host
    // does not know what lives there, and a session that is told where to look can go and look.
    //
    // Every piece is put on one line before it is said (`ProjectPromptText.OneLine`): the value is
    // operator-typed and only ever trimmed upstream, and the title and instruction are a plugin's free text, so
    // none of the three come pre-guaranteed to be a single line the way an `ProjectInfoField` row is by
    // the time it gets here. A pasted line break would otherwise arrive in the standing instructions as a fresh
    // line the session reads as an instruction of its own.
    private static string? _SingleMemorySentence(string reference, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = ProjectPromptText.OneLine(reference.Trim());
        if (memorySources is { Count: > 0 }
            && ProjectMemoryRef.TryParse(trimmed, out var scheme, out var value)
            && memorySources.FirstOrDefault(source => string.Equals(source.Scheme, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched
            && ProjectPromptText.OneLine(matched.Title.Trim()) is { Length: > 0 } title)
        {
            var tidiedValue = ProjectPromptText.OneLine(value);
            var located = _CappedSentence($"This project's memory lives in {title} \"", tidiedValue, "\".");
            var instruction = ProjectPromptText.OneLine(matched.Instruction.Trim());
            if (instruction.Length == 0)
            {
                // The registry refuses a source without an instruction, so reaching this is a caller that built its
                // own list. Say where the memory is and stop, rather than trailing a lone full stop behind the place.
                return located;
            }

            // An instruction that already ends its own sentence keeps its own full stop rather than getting a second.
            var sentence = _SentenceEndings.Contains(instruction[^1]) ? instruction : $"{instruction}.";
            var withInstruction = $"{located} {sentence}";

            // The instruction is a plugin's free text with no length limit upstream, so the combined sentence might
            // not fit even though the place alone (already capped above) does. Cutting an instruction in half can
            // flip what it means — "do not delete the old notes" becomes "do not delete" — so a too-long instruction
            // is left out whole rather than clipped: the place is still said, just not how to use it.
            return withInstruction.Length <= _PlaceNameBudget ? withInstruction : located;
        }

        return _CappedSentence(
            "This project's memory lives at ",
            trimmed,
            ". Read it there when you need what this project already knows, and keep it up to date as you work.");
    }

    // `prefix` + `value` + `suffix`, cut down to
    // `_PlaceNameBudget` if it does not fit. `value` is the one part shortened first:
    // it is a name — a path, a project key — not an instruction, so unlike an instruction it is safe to cut rather
    // than having to be dropped whole. The cut is marked in place, in the spirit of `_InformationNote`'s
    // "(and N more that did not fit here…)": the session, and the operator reading the prompt back, should be able
    // to see that the location shown is not the whole of it.
    //
    // AC-484 confirming round (MUST-FIX 1): `prefix` is capped the same way when it alone already
    // leaves no room for a value — before this fix only `value` was ever bounded, so a
    // `prefix` built from unbounded plugin text (`_PlaceName` and
    // `_SingleMemorySentence` both fold a `Projects.ProjectMemorySource.Title` into theirs)
    // could grow without limit while this method kept insisting it never returned more than
    // `_PlaceNameBudget` characters: once `prefix` alone reached that length, the
    // computed `available` room for `value` clamped to zero and the method handed back
    // `prefix` whole, plus a marker and `suffix` — exactly as unbounded as the
    // value cut was supposed to prevent. A normal call is unaffected: `prefix` only takes this
    // path when it does not fit the budget on its own, which never happens for the short literal prefixes this
    // class builds (a handful of words and a quote) or for an ordinary `Title`.
    private static string _CappedSentence(string prefix, string value, string suffix)
    {
        var sentence = $"{prefix}{value}{suffix}";
        if (sentence.Length <= _PlaceNameBudget)
        {
            return sentence;
        }

        const string truncationMarker = " (truncated)";

        // The prefix itself might already be the whole reason this does not fit — an unbounded plugin Title folded
        // into it, for instance — in which case there is no room left for any of the value at all, and cutting the
        // prefix in place (the same visible marker the value gets) is the only way to keep the promise this method
        // makes rather than handing the whole of an unbounded prefix straight through.
        var maxPrefixLength = Math.Max(0, _PlaceNameBudget - suffix.Length - truncationMarker.Length);
        if (prefix.Length > maxPrefixLength)
        {
            return $"{prefix[..maxPrefixLength]}{truncationMarker}{suffix}";
        }

        var available = Math.Max(0, _PlaceNameBudget - prefix.Length - suffix.Length - truncationMarker.Length);
        var shortenedValue = value[..Math.Min(value.Length, available)] + truncationMarker;
        return $"{prefix}{shortenedValue}{suffix}";
    }

    // A block asking the session to follow this project's standing instructions, naming where they are kept, and —
    // for a row that ticked `ProjectResource.SendsContent` and whose content `instructionContents`
    // actually holds — carrying that content along too (AC-486). One row names it directly; more than one row is
    // asked to follow all of them, listed — as many as fit `budget`, the rest dropped and
    // announced rather than growing the sentence without limit (AC-484 review, MUST-FIX 1).
    //
    // The give-up order for a tight budget (AC-486, step 1 of the order documented on
    // `ProjectContributionBudget`): for the full set of rows, content is tried first
    // (`_InstructionsNoteText` with `includeContent: true`); only if that still does not fit is
    // content given up for that same set of rows (`_WithoutInstructionContent`) before a row is ever
    // dropped to make room. Both are tried again at each smaller row count in turn — the same prefix-of-rows rule
    // `_FitRowsToBudget` already applies elsewhere, just with two renderings per count instead of one,
    // since content is now something this block can give up on its own before a whole row goes.
    private static string? _InstructionsNote(
        IReadOnlyList<ProjectResource> instructionRows,
        IReadOnlyDictionary<string, string>? instructionContents,
        IReadOnlyCollection<string>? unresolvedReferences,
        int budget)
    {
        if (instructionRows.Count == 0)
        {
            return null;
        }

        var effectiveBudget = Math.Max(0, budget);
        for (var count = instructionRows.Count; count > 0; count--)
        {
            var kept = instructionRows.Take(count).ToList();
            var dropped = instructionRows.Count - count;

            var withContent = _InstructionsNoteText(kept, instructionContents, unresolvedReferences, dropped, includeContent: true);
            if (withContent.Length <= effectiveBudget)
            {
                return withContent;
            }

            var withoutContent = _WithoutInstructionContent(kept, unresolvedReferences, dropped);
            if (withoutContent.Length <= effectiveBudget)
            {
                return withoutContent;
            }
        }

        return null;
    }

    // The actual rendering both `_InstructionsNote` (with content) and `_WithoutInstructionContent`
    // (without) build from — the unchanged location-naming sentence this class has always produced for
    // `kept`, followed by one content block per row that ticked
    // `ProjectResource.SendsContent`: its file's content when `includeContent` is true
    // and `instructionContents` actually holds an entry for that row's
    // `ProjectResource.Reference` (`_ContentBlock`), or otherwise a short notice that the
    // content it was ticked to carry did not make it into this prompt — the same notice whether the reader could
    // not read the file at all (missing, too large, unreadable) or `includeContent` is false
    // because the whole block did not fit the budget with content included. A session must never be left thinking
    // it saw a file's contents when it only ever got told where the file is (AC-486): the one thing worse than
    // content not making it in is not saying so.
    private static string _InstructionsNoteText(
        IReadOnlyList<ProjectResource> kept,
        IReadOnlyDictionary<string, string>? instructionContents,
        IReadOnlyCollection<string>? unresolvedReferences,
        int dropped,
        bool includeContent)
    {
        var places = kept.Select(_ResourceDisplay).ToList();
        var sentence = places.Count == 1
            ? $"This project keeps standing instructions at {places[0]}. Read them and follow them for the rest of this session."
            : $"This project keeps standing instructions in {_JoinWithAnd(places)}. Read them and follow them all for the rest of this session.";

        var missing = kept
            .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
            .Select(_ResourceDisplay)
            .ToList();
        var head = sentence + _NotFoundSuffix(missing) + _DroppedRowsSuffix(dropped);

        var contentBlocks = new List<string>();
        for (var index = 0; index < kept.Count; index++)
        {
            var row = kept[index];
            if (!row.SendsContent)
            {
                continue;
            }

            var place = places[index];
            contentBlocks.Add(includeContent && instructionContents is not null && instructionContents.TryGetValue(row.Reference, out var content)
                ? _ContentBlock(place, content)
                : $"{place} was ticked to send its content along, but it is not included in this prompt — read it directly at that location.");
        }

        return contentBlocks.Count == 0 ? head : $"{head}\n\n{string.Join("\n\n", contentBlocks)}";
    }

    // A single Instructions row's file content, said as a labelled snapshot rather than dropped in unannounced
    // (AC-486): named by `place` so the citation matches the location sentence above it, marked
    // explicitly as a copy taken at session start rather than a live view, and pointed back at
    // `place` for the current version — the failure this guards against is a session that thinks
    // it has seen this project's conventions in full when it was only ever handed a path (Raymond's own framing for
    // AC-486: accept that the copy can go stale, and say so, rather than pretend it cannot).
    private static string _ContentBlock(string place, string content) =>
        $"{place}'s content, captured below as it stood at the start of this session (a snapshot, not a live view — " +
        $"reread {place} for the current version if you suspect it has since changed):\n\n{content}";

    // A block saying this project keeps material worth looking things up in — never obeyed, never written to,
    // the same distinction `ProjectResourceRole.Reference` draws. One row names it directly; more than
    // one row lists all of them — as many as fit `budget`, the rest dropped and announced rather
    // than growing the sentence without limit (AC-484 review, MUST-FIX 1; see `_FitRowsToBudget`).
    private static string? _ReferenceNote(IReadOnlyList<ProjectResource> referenceRows, IReadOnlyCollection<string>? unresolvedReferences, int budget)
    {
        if (referenceRows.Count == 0)
        {
            return null;
        }

        return _FitRowsToBudget(referenceRows, Math.Max(0, budget), (kept, dropped) =>
        {
            var places = kept.Select(_ResourceDisplay).ToList();
            var sentence = places.Count == 1
                ? $"This project keeps reference material at {places[0]} — look things up there when you need an answer."
                : $"This project keeps reference material in {_JoinWithAnd(places)} — look things up there when you need an answer.";

            var missing = kept
                .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
                .Select(_ResourceDisplay)
                .ToList();
            return sentence + _NotFoundSuffix(missing) + _DroppedRowsSuffix(dropped);
        });
    }

    // Fits a row-listing block (Instructions, Reference, or the multi-row Memory sentence) inside
    // `budget` by dropping whole rows from the end, never by slicing the sentence
    // `render` builds around them — the same AC-166 rule `_SingleMemorySentence`
    // already applies to an overlong instruction ("do not delete the old notes" must never become "do not
    // delete" by being cut mid-word). Tries every row first; if that does not fit, tries all but the last, and so
    // on, so the rows kept are always a prefix of `rows` — the ones the operator entered first,
    // consistent with how `_InformationNote` already keeps a prefix of its own rows.
    //
    // AC-484 review (MUST-FIX 1): before this, none of the three row-listing blocks had any per-block ceiling at
    // all — each was built once, at full length, and only measured afterwards. A single absurdly long row (an
    // operator-typed `ProjectResource.Label` has no length limit upstream) or simply many ordinary
    // rows could each make the sentence this method now bounds grow past the shared ceiling on its own.
    //
    // Returns null when even a single row does not fit — nothing here to introduce a list of "N more" without at
    // least one row actually named, so the whole block drops rather than showing an admission with nothing above
    // it. Reachable only under a budget too small to hold even one row, which given the assignment order
    // documented on `ProjectContributionBudget` means the blocks ahead of this one in that order have
    // already spent nearly the entire shared ceiling.
    private static string? _FitRowsToBudget(
        IReadOnlyList<ProjectResource> rows,
        int budget,
        Func<IReadOnlyList<ProjectResource>, int, string> render)
    {
        for (var count = rows.Count; count > 0; count--)
        {
            var candidate = render(rows.Take(count).ToList(), rows.Count - count);
            if (candidate.Length <= budget)
            {
                return candidate;
            }
        }

        return null;
    }

    // The clause that owns up to whole rows `_FitRowsToBudget` dropped rather than naming — the prose
    // counterpart to `_Admission`'s bullet line for the information block, said the same way: a
    // dropped row is announced, never silently gone.
    private static string _DroppedRowsSuffix(int dropped) => dropped switch
    {
        0 => string.Empty,
        1 => " One more did not fit here — read it in the project itself.",
        _ => $" {dropped} more did not fit here — read them in the project itself.",
    };

    // The project's own information rows that the operator ticked to share (AC-314), as one labelled block — never a
    // row marked secret, whatever its sharing flag says (AC-318). Null when none apply — which is the default, so a
    // session's prompt does not grow because a project happens to keep notes.
    //
    // Told as flat `label: value` lines rather than a sentence per row: the operator wrote these labels, and
    // rephrasing them into prose would put words in their mouth. A row they left unlabelled is given as the bare
    // value.
    //
    // Each row is tidied here rather than trusted to have been, even though the store tidies on load and on save. This
    // is one line per row: a value that still held a line break would arrive as extra lines the session reads as
    // instructions of their own, and relying on an earlier caller to have prevented that is how a guard stops being
    // one.
    //
    // `budget`:
    // How much of the shared `ProjectContributionBudget` is left for this block once the parts that
    // go ahead of it in the assignment order (Memory, Instructions, Reference) have taken their share — see the
    // order documented on that constant. Never the fixed ceiling this block used to answer to on its own.
    private static string? _InformationNote(Project? project, int budget)
    {
        var shared = project?.AdditionalInfo
            .Where(field => field.ReachesSessions)
            .Select(field => field.Tidied())
            .Where(field => !field.IsBlank)
            .ToList() ?? [];

        if (shared.Count == 0)
        {
            return null;
        }

        // The heading and the newline joining it to the first row are part of what this block costs, so they come
        // off the budget before a single row is measured against it. Leaving them out is how a ceiling ends up
        // being passed by exactly the length of the things nobody counted.
        const string heading = "What else you should know about this project:";
        var available = Math.Max(0, budget - heading.Length - 1);
        if (available <= 0)
        {
            // AC-484 review (MUST-FIX 3): nothing can follow the heading at all, so the heading does not go out
            // either — a heading over an empty list is worse than saying nothing about this block.
            return null;
        }

        var lines = new List<string>();
        var used = 0;
        foreach (var field in shared)
        {
            // A label the operator already punctuated keeps its own colon rather than getting a second one.
            var label = field.Label.EndsWith(':') ? field.Label : $"{field.Label}:";
            var prefix = field.HasLabel ? $"- {label} " : "- ";
            var line = $"{prefix}{field.Value}";

            // AC-484 review (MUST-FIX 3): the very first row used to be added however long, because the check
            // below that stops later rows only fired once `lines.Count > 0`. A single field whose own value ran
            // past the whole budget therefore always passed straight through, alone, regardless of `budget`. A
            // line too long to fit at all is now shortened first — it is a value the operator shared, not an
            // instruction, so cutting it (with a visible marker) is the same safe move
            // <see cref="_CappedSentence"/> already makes for an overlong memory place.
            if (line.Length > available)
            {
                const string marker = " (truncated)";
                var roomForValue = available - prefix.Length - marker.Length;
                if (roomForValue > 0)
                {
                    line = $"{prefix}{field.Value[..Math.Min(field.Value.Length, roomForValue)]}{marker}";
                }
            }

            if (used + line.Length > available)
            {
                // This row — the first as much as any other, now that the special case above is gone — does not
                // fit even on its own. Stop rather than skip ahead: a row that did not fit is reported by the
                // admission below, not silently passed over in favor of a shorter one further down the list.
                break;
            }

            lines.Add(line);
            used += line.Length + 1;
        }

        // Said out loud rather than trimmed away in silence: the session is told its picture is incomplete, and the
        // operator can see in the prompt that a row they ticked did not make it.
        //
        // The admission has to fit inside the budget too, which means giving rows back until it does — and its own
        // length moves while that happens, because the number in it grows as rows are dropped. Recomputed each time
        // round rather than measured once: an admission that itself overruns the ceiling would be the one line in
        // this block guaranteed to be wrong about the very thing it is there to report.
        if (lines.Count < shared.Count)
        {
            var admission = _Admission(shared.Count - lines.Count);
            while (lines.Count > 0 && used + admission.Length > available)
            {
                used -= lines[^1].Length + 1;
                lines.RemoveAt(lines.Count - 1);
                admission = _Admission(shared.Count - lines.Count);
            }

            // AC-484 review (MUST-FIX 3): the admission used to be added unconditionally, even once every row had
            // been given back to make room for it — with an empty `lines` and a budget too small for even the
            // admission itself, that produced the heading followed by a single line that itself overran the
            // ceiling. Left out entirely rather than shown broken: a budget this tight means nothing about this
            // block can be said at all.
            if (admission.Length <= available)
            {
                lines.Add(admission);
            }
        }

        return lines.Count == 0 ? null : $"{heading}\n{string.Join('\n', lines)}";
    }

    // The line that owns up to the rows this block could not carry. Its own length is part of the budget, which is
    // why it exists as a function: the caller has to be able to ask what it would cost for a given count before
    // deciding whether that count is affordable.
    private static string _Admission(int dropped) =>
        $"- (and {dropped} more that did not fit here — read them in the project itself)";

    // The profile's standing instructions with the project's appended under them, blank-separated. Both apply and
    // neither replaces the other: the profile says who the session is, the project what it is working on. Order
    // matters — identity first, then the task, so the more specific instruction is the last thing read.
    private static string? _JoinPrompts(params string?[] prompts)
    {
        var parts = prompts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
