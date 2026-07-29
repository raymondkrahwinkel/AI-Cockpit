using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Sessions;

/// <summary>
/// What a new session opens with once a project and a profile have both had their say (AC-158/AC-159), and the
/// only place the two meet. A project is an override on top of a profile: where both answer the same question
/// the project wins, where it stays silent the profile's default stands, and where neither speaks the app
/// default applies.
/// <para>
/// One resolver rather than the rule repeated per caller, because the same question is asked from the New-session
/// dialog, the launcher and the sidebar's quick-start. Three copies of a precedence rule are three chances for
/// them to disagree, and then what a session starts with depends on which door it came through.
/// </para>
/// </summary>
/// <param name="WorkingDirectory">The folder to start in; null/blank leaves the caller on its own default.</param>
/// <param name="IsolateInWorktree">Whether to isolate in a git worktree (AC-85) when the folder is a repository. Still a per-session choice — this only pre-selects it.</param>
/// <param name="ProfileLabel">The profile to preselect, by label; null leaves the dialog's own selection alone.</param>
/// <param name="EnabledMcpServerNames">
/// Which servers open ticked for a session started <em>without</em> a project — the profile's saved selection, or
/// null for no restriction. A project answers this itself (<see cref="ProjectMcpOverlay.IsSelectedByDefault"/>) and
/// its answer wins, so this is the fallback rather than the resolved value.
/// </param>
/// <param name="SystemPrompt">
/// The standing instructions to append to the provider's own system prompt: the profile's identity first
/// (AC-142), then the project's own <see cref="Project.BehaviorPrompt"/>, then its <see cref="Project.Resources"/>
/// rows — standing instructions first, then where its memory lives, then what it may merely look up — and last
/// whatever the operator shared as plain information. Null when none of them have anything to say.
/// </param>
public sealed record SessionStartDefaults(
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? ProfileLabel,
    IReadOnlyList<string>? EnabledMcpServerNames,
    string? SystemPrompt)
{
    /// <summary>
    /// The defaults for starting under <paramref name="project"/> and <paramref name="profile"/>, either of which
    /// may be absent — a session without a project is how the cockpit has always started one.
    /// </summary>
    /// <param name="globalWorkingDirectory">The configured app-wide working directory, used when neither the project nor the profile names one.</param>
    /// <param name="memorySources">
    /// The memory sources plugins have registered (AC-165/166, <c>ICockpitHost.AddProjectMemorySource</c>), so a
    /// <see cref="ProjectResourceRole.Memory"/> row naming one of them is explained rather than merely quoted. Null
    /// (the default) is exactly "none registered" — every caller that does not yet pass this list gets the plain,
    /// unexplained sentence it always got, unchanged.
    /// </param>
    /// <param name="unresolvedReferences">
    /// The <see cref="ProjectResource.Reference"/> values a caller has already checked and found missing (AC-484) —
    /// deliberately an input, not something this method goes and finds out for itself. Resolving whether a
    /// reference exists is I/O, and purity is a property this class keeps on purpose: the same rule is asked from
    /// three different surfaces, and a resolver that sometimes touches disk and sometimes does not is one more way
    /// for those three to disagree, this time depending on what the filesystem happened to look like at the moment
    /// each was called. So a caller assembling an actual launch (<c>ProjectQuickStart</c>, the New-session dialog's
    /// Start) runs its own small probe first and hands the result in as plain data; a caller only previewing a
    /// field (a project or profile picker updating the working-directory box) can reasonably skip that and pass
    /// null, which reads as "nothing known to be missing" rather than "nothing is missing" — the difference matters
    /// only in that the former never mentions a broken reference, never that it blocks one.
    /// </param>
    /// <remarks>
    /// The MCP selection here stays the profile's, and that is not a gap in "the project wins": a project's
    /// selection is a per-server answer rather than a list (<see cref="ProjectMcpOverlay.IsSelectedByDefault"/>),
    /// applied where the checklist is built, and it beats this one wherever a project is in play. Resolving it into
    /// a list here would need the catalog — which this rule deliberately knows nothing about.
    /// </remarks>
    public static SessionStartDefaults Resolve(
        Project? project,
        SessionProfile? profile,
        string? globalWorkingDirectory = null,
        IReadOnlyList<ProjectMemorySource>? memorySources = null,
        IReadOnlyCollection<string>? unresolvedReferences = null)
    {
        // A row switched off (ReachesSessions = false) is filtered out before any block below ever sees it — the
        // one place that rule is honored, rather than each block having to remember to check it itself.
        var reachable = project?.Resources.Where(resource => resource.ReachesSessions).ToList()
            ?? (IReadOnlyList<ProjectResource>)[];
        var memoryRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Memory).ToList();
        var instructionRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Instructions).ToList();
        var referenceRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Reference).ToList();

        // Give-up order, step 1: an Instructions row's file *content* would be dropped back to its location-only
        // sentence here first, before anything else gives way — AC-486 is the work that teaches this block to
        // carry a file's content in the first place. Until it does there is nothing here to drop, so this call is
        // a deliberate no-op today; it stays in the pipeline, named, so AC-486 has a slot to fill rather than
        // having to invent the step from scratch.
        var instructionsNote = _WithoutInstructionContent(_InstructionsNote(instructionRows, unresolvedReferences));
        var memoryNote = _MemoryNote(memoryRows, memorySources, unresolvedReferences);
        var referenceNote = _ReferenceNote(referenceRows, unresolvedReferences);

        // Give-up order, step 2: the information rows are the one part of a project's contribution that is built
        // to shrink row by row (and always was, see _InformationNote). Everything that never shrinks — step 1's
        // (currently absent) instruction content aside — has already had its say above, so whatever is left of the
        // shared ceiling is what the information block gets to work with.
        var reserved = _ReservedLength(instructionsNote) + _ReservedLength(memoryNote) + _ReservedLength(referenceNote);
        var informationBudget = Math.Max(0, ProjectContributionBudget - reserved);
        var informationNote = _InformationNote(project, informationBudget);

        return new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames,
            // Order matters, most binding first — the same reasoning _JoinPrompts always applied ("identity first,
            // then the task"), carried one level further now there is more than one kind of task-level note: the
            // profile says who the session is, the project's BehaviorPrompt what it is working on, its Instructions
            // rows what it must actively obey, its memory rows where to go read and write what it already knows,
            // its Reference rows what it may merely look up, and last — least binding of all, since it is material
            // the operator shared for reference rather than anything to act on — whatever else was ticked to share.
            _JoinPrompts(profile?.SystemPrompt, project?.BehaviorPrompt, instructionsNote, memoryNote, referenceNote, informationNote));
    }

    /// <summary>
    /// How much of the standing instructions a project's own contribution — its Instructions, Memory and Reference
    /// rows together with its shared information rows — may take, replacing the two separate ceilings
    /// (<c>InformationNoteBudget</c> of 4000, <c>MemoryNoteBudget</c> of 1500) this class used to keep. One shared
    /// ceiling rather than two independent ones because nothing bounded their sum: with only a memory sentence and
    /// an information block, two unrelated caps happened to be enough, but AC-484 adds two more blocks that grow
    /// the same way, and two caps that do not know about each other do not stop the total from growing without
    /// limit — only ever bounding each block in isolation. The reason a ceiling exists at all has not changed
    /// (see the constant this one replaces): the Claude route hands the whole prompt to its CLI as one process
    /// argument, and a command line has a hard limit, so an unbounded contribution does not merely cost budget, it
    /// stops the session starting at all.
    /// <para>
    /// 6000 rather than simply 4000 + 1500 = 5500: the two old ceilings summed is the reasonable floor this value
    /// starts from, but it bounded one memory sentence and one information block, and there are now two further
    /// blocks (Instructions, Reference) that did not exist before and were not part of that sum — each is normally
    /// a short sentence or two, so a few hundred extra characters of headroom covers a realistic project's worth of
    /// them without materially loosening the ceiling's purpose. Still nowhere near the length that would trouble a
    /// process command line, and still comfortably enforced by the give-up order below rather than trusted.
    /// </para>
    /// </summary>
    private const int ProjectContributionBudget = 6000;

    /// <summary>
    /// How long a single <see cref="ProjectResourceRole.Memory"/> row's own rendered sentence may run before its
    /// value is cut, kept separate from <see cref="ProjectContributionBudget"/> on purpose: that budget bounds how
    /// the several blocks of a project's contribution share one ceiling, while this one bounds a single value's own
    /// rendering regardless of how much of the shared ceiling happens to be free. Folding this into the shared
    /// budget would let one absurdly long path alone consume nearly all of it, leaving nothing for the Instructions
    /// or Reference blocks even though the pathological value itself never needed six thousand characters — a
    /// bigger shared ceiling should buy more *blocks* room, not license one value to grow into all of it. 1500 is
    /// the same figure this class has always used here (see the git history this constant replaces): a realistic
    /// memory location is a path or a project key, at most a few hundred characters, and a realistic source
    /// instruction is a sentence or two a plugin author wrote by hand — 1500 leaves room for both together several
    /// times over while still refusing to let either grow without limit.
    /// </summary>
    private const int _MemorySentenceBudget = 1500;

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    /// <summary>An estimate of how much of the shared budget a note actually costs once joined: its own length plus the blank line <see cref="_JoinPrompts"/> puts before the next part. Null/blank costs nothing — it does not appear in the joined prompt at all.</summary>
    private static int _ReservedLength(string? note) => string.IsNullOrEmpty(note) ? 0 : note.Length + 2;

    /// <summary>Whether <paramref name="reference"/> is one a caller's probe already found missing — see <see cref="Resolve"/>'s remarks on <c>unresolvedReferences</c> for why this class never checks that itself.</summary>
    private static bool _IsUnresolved(string reference, IReadOnlyCollection<string>? unresolvedReferences) =>
        unresolvedReferences is { Count: > 0 } && unresolvedReferences.Contains(reference);

    /// <summary>
    /// <paramref name="missingPlaceNames"/> said out loud rather than silently — the same courtesy
    /// <see cref="_InformationNote"/> gives a row that did not fit, applied here to a reference that could not be
    /// found at all. Never blocks anything it is attached to: an agent that thinks a place holds no conventions
    /// behaves differently from one that knows it simply could not read them, so the gap is named, and the session
    /// starts regardless — the same line the bundled-plugin installer draws ("a convenience, not a dependency").
    /// </summary>
    private static string _NotFoundSuffix(IReadOnlyList<string> missingPlaceNames) => missingPlaceNames.Count switch
    {
        0 => string.Empty,
        1 => $" {missingPlaceNames[0]} could not be found there — the session starts anyway; check the location when you get a chance.",
        _ => $" The following could not be found: {_JoinWithAnd(missingPlaceNames)} — the session starts anyway; check them when you get a chance.",
    };

    /// <summary><paramref name="items"/> joined as an English list: empty for none, the one item alone, "a and b" for two, "a, b and c" for more.</summary>
    private static string _JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}",
    };

    /// <summary>
    /// Step 1 of the give-up order documented on <see cref="ProjectContributionBudget"/>: when the shared budget is
    /// tight, an Instructions row's file <em>content</em> should be the first thing dropped back to its
    /// location-only sentence — content is the least essential part of an instructions block (the session can
    /// still be told where to go read it itself), where the memory sentence is the one thing a session cannot do
    /// without at all. AC-486 is the work that gives this block content to read in the first place; until then
    /// <paramref name="instructionsNote"/> never carries any, so there is nothing to drop and this is a deliberate
    /// no-op — kept as a named step rather than left out, so the budget pipeline already has the slot AC-486 needs
    /// instead of that work having to invent one.
    /// </summary>
    private static string? _WithoutInstructionContent(string? instructionsNote) => instructionsNote;

    /// <summary>
    /// Sentence endings that count as "already punctuated" for <see cref="_SingleMemorySentence"/> — the same idea
    /// <see cref="_InformationNote"/> applies to a label's trailing colon, just for a sentence rather than a word.
    /// </summary>
    private static readonly char[] _SentenceEndings = ['.', '!', '?'];

    /// <summary>
    /// How a single <see cref="ProjectResourceRole.Memory"/> row's <see cref="ProjectResource.Reference"/> is said
    /// back to the session, for whichever <paramref name="memorySources"/> row (if any) explains it — named via
    /// that source's own <c>Title</c>, without its <c>Instruction</c>. Used both to render the single-row memory
    /// sentence (<see cref="_SingleMemorySentence"/>) and to name a row in a multi-row sentence or a not-found
    /// mention, so the two say the same place the same way.
    /// </summary>
    private static string _PlaceName(string reference, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        var trimmed = ProjectPromptText.OneLine(reference.Trim());
        if (memorySources is { Count: > 0 }
            && ProjectMemoryRef.TryParse(trimmed, out var scheme, out var value)
            && memorySources.FirstOrDefault(source => string.Equals(source.Scheme, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched
            && ProjectPromptText.OneLine(matched.Title.Trim()) is { Length: > 0 } title)
        {
            return $"{title} \"{ProjectPromptText.OneLine(value)}\"";
        }

        return trimmed;
    }

    /// <summary>What an Instructions or Reference row is called back to the session: the operator's own <see cref="ProjectResource.Label"/> when they gave one, the bare reference otherwise — the same choice <see cref="Projects.ProjectInfoField"/> makes for an unlabelled row.</summary>
    private static string _ResourceDisplay(ProjectResource row) =>
        !string.IsNullOrWhiteSpace(row.Label)
            ? ProjectPromptText.OneLine(row.Label.Trim())
            : ProjectPromptText.OneLine(row.Reference.Trim());

    /// <summary>
    /// Where a project's memory lives, said in a sentence the session can act on. Null when it keeps none.
    /// <para>
    /// One row says it exactly the way this class always has (<see cref="_SingleMemorySentence"/>) — that
    /// byte-for-byte match is deliberate: a project with a single memory row is by far the common case, and every
    /// caller and every test written against the old single-<c>MemoryRef</c> world keeps working unchanged.
    /// </para>
    /// <para>
    /// More than one row says so in a single sentence naming all of them, plus a second sentence on which channel
    /// to use for what: the local folder for searching, bulk reading and working offline, the MCP or remote source
    /// for the current shared state. Reading may draw from either; writing should not cross channels within one
    /// session — an agent that writes the same file through both meets its own edit again at the next sync.
    /// </para>
    /// </summary>
    private static string? _MemoryNote(
        IReadOnlyList<ProjectResource> memoryRows,
        IReadOnlyList<ProjectMemorySource>? memorySources,
        IReadOnlyCollection<string>? unresolvedReferences)
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
            return sentence + _NotFoundSuffix(missing);
        }

        var places = memoryRows.Select(row => _PlaceName(row.Reference, memorySources)).ToList();
        var missingPlaces = memoryRows
            .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
            .Select(row => _PlaceName(row.Reference, memorySources))
            .ToList();

        return
            $"This project's memory lives in {_JoinWithAnd(places)}. " +
            "Use the local folder to search it, read it in bulk and work offline; use the MCP or remote source " +
            "for the current shared state. Reading may draw from either channel, but prefer writing any one file " +
            "through a single channel within a session — write the same file through both and the next sync " +
            "collides with your own edit." +
            _NotFoundSuffix(missingPlaces);
    }

    /// <summary>
    /// The single-row memory sentence, unchanged from the code AC-484 found here (only the input shape changed,
    /// from <c>Project.MemoryRef</c> straight to a resource's <see cref="ProjectResource.Reference"/> — the two are
    /// the same string for the row this is called with).
    /// <para>
    /// A reference of the shape <c>&lt;scheme&gt;:&lt;value&gt;</c> naming a registered <paramref name="memorySources"/>
    /// entry is explained — named by that source's own <c>Title</c>, with its <c>Instruction</c> appended so the
    /// session is told how to reach it, not only where it is. Anything else — a bare path, a scheme nothing
    /// registered, an empty value after the colon, a matched source whose <c>Title</c> is itself blank — falls back
    /// to the plain, unexplained sentence this always said: deliberately told rather than loaded, because the host
    /// does not know what lives there, and a session that is told where to look can go and look.
    /// </para>
    /// <para>
    /// Every piece is put on one line before it is said (<see cref="ProjectPromptText.OneLine"/>): the value is
    /// operator-typed and only ever trimmed upstream, and the title and instruction are a plugin's free text, so
    /// none of the three come pre-guaranteed to be a single line the way an <see cref="ProjectInfoField"/> row is by
    /// the time it gets here. A pasted line break would otherwise arrive in the standing instructions as a fresh
    /// line the session reads as an instruction of its own.
    /// </para>
    /// </summary>
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
            return withInstruction.Length <= _MemorySentenceBudget ? withInstruction : located;
        }

        return _CappedSentence(
            "This project's memory lives at ",
            trimmed,
            ". Read it there when you need what this project already knows, and keep it up to date as you work.");
    }

    /// <summary>
    /// <paramref name="prefix"/> + <paramref name="value"/> + <paramref name="suffix"/>, cut down to
    /// <see cref="_MemorySentenceBudget"/> if it does not fit. Only <paramref name="value"/> is ever shortened: it
    /// is a name — a path, a project key — not an instruction, so unlike an instruction it is safe to cut rather
    /// than having to be dropped whole. The cut is marked in place, in the spirit of <see cref="_InformationNote"/>'s
    /// "(and N more that did not fit here…)": the session, and the operator reading the prompt back, should be able
    /// to see that the location shown is not the whole of it.
    /// </summary>
    private static string _CappedSentence(string prefix, string value, string suffix)
    {
        var sentence = $"{prefix}{value}{suffix}";
        if (sentence.Length <= _MemorySentenceBudget)
        {
            return sentence;
        }

        const string truncationMarker = " (truncated)";
        var available = Math.Max(0, _MemorySentenceBudget - prefix.Length - suffix.Length - truncationMarker.Length);
        var shortenedValue = value[..Math.Min(value.Length, available)] + truncationMarker;
        return $"{prefix}{shortenedValue}{suffix}";
    }

    /// <summary>
    /// A block asking the session to follow this project's standing instructions, naming where they are kept —
    /// content-free today (AC-484 is scoped to the sentence, not reading the file; AC-486 is what teaches this
    /// class to read one). One row names it directly; more than one row is asked to follow all of them, listed.
    /// </summary>
    private static string? _InstructionsNote(IReadOnlyList<ProjectResource> instructionRows, IReadOnlyCollection<string>? unresolvedReferences)
    {
        if (instructionRows.Count == 0)
        {
            return null;
        }

        var places = instructionRows.Select(_ResourceDisplay).ToList();
        var sentence = instructionRows.Count == 1
            ? $"This project keeps standing instructions at {places[0]}. Read them and follow them for the rest of this session."
            : $"This project keeps standing instructions in {_JoinWithAnd(places)}. Read them and follow them all for the rest of this session.";

        var missing = instructionRows
            .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
            .Select(_ResourceDisplay)
            .ToList();
        return sentence + _NotFoundSuffix(missing);
    }

    /// <summary>
    /// A block saying this project keeps material worth looking things up in — never obeyed, never written to,
    /// the same distinction <see cref="ProjectResourceRole.Reference"/> draws. One row names it directly; more than
    /// one row lists all of them.
    /// </summary>
    private static string? _ReferenceNote(IReadOnlyList<ProjectResource> referenceRows, IReadOnlyCollection<string>? unresolvedReferences)
    {
        if (referenceRows.Count == 0)
        {
            return null;
        }

        var places = referenceRows.Select(_ResourceDisplay).ToList();
        var sentence = referenceRows.Count == 1
            ? $"This project keeps reference material at {places[0]} — look things up there when you need an answer."
            : $"This project keeps reference material in {_JoinWithAnd(places)} — look things up there when you need an answer.";

        var missing = referenceRows
            .Where(row => _IsUnresolved(row.Reference, unresolvedReferences))
            .Select(_ResourceDisplay)
            .ToList();
        return sentence + _NotFoundSuffix(missing);
    }

    /// <summary>
    /// The project's own information rows that the operator ticked to share (AC-314), as one labelled block — never a
    /// row marked secret, whatever its sharing flag says (AC-318). Null when none apply — which is the default, so a
    /// session's prompt does not grow because a project happens to keep notes.
    /// <para>
    /// Told as flat <c>label: value</c> lines rather than a sentence per row: the operator wrote these labels, and
    /// rephrasing them into prose would put words in their mouth. A row they left unlabelled is given as the bare
    /// value.
    /// </para>
    /// <para>
    /// Each row is tidied here rather than trusted to have been, even though the store tidies on load and on save. This
    /// is one line per row: a value that still held a line break would arrive as extra lines the session reads as
    /// instructions of their own, and relying on an earlier caller to have prevented that is how a guard stops being
    /// one.
    /// </para>
    /// </summary>
    /// <param name="budget">
    /// How much of the shared <see cref="ProjectContributionBudget"/> is left for this block once the parts that
    /// never shrink (Instructions, Memory, Reference) have taken their share — see the give-up order documented on
    /// that constant. Never the fixed ceiling this block used to answer to on its own.
    /// </param>
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

        var lines = new List<string>();
        var remaining = budget;
        foreach (var field in shared)
        {
            // A label the operator already punctuated keeps its own colon rather than getting a second one.
            var label = field.Label.EndsWith(':') ? field.Label : $"{field.Label}:";
            var line = field.HasLabel ? $"- {label} {field.Value}" : $"- {field.Value}";
            if (line.Length > remaining && lines.Count > 0)
            {
                break;
            }

            lines.Add(line);
            remaining -= line.Length + 1;
        }

        // Said out loud rather than trimmed away in silence: the session is told its picture is incomplete, and the
        // operator can see in the prompt that a row they ticked did not make it.
        if (lines.Count < shared.Count)
        {
            lines.Add($"- (and {shared.Count - lines.Count} more that did not fit here — read them in the project itself)");
        }

        return $"What else you should know about this project:\n{string.Join('\n', lines)}";
    }

    /// <summary>
    /// The profile's standing instructions with the project's appended under them, blank-separated. Both apply and
    /// neither replaces the other: the profile says who the session is, the project what it is working on. Order
    /// matters — identity first, then the task, so the more specific instruction is the last thing read.
    /// </summary>
    private static string? _JoinPrompts(params string?[] prompts)
    {
        var parts = prompts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }
}
