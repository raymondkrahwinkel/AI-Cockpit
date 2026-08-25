using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;

namespace Cockpit.Core.Sessions;

// AC-158/AC-159: what a new session opens with once a project and a profile have both had their say — a
// project overrides a profile where both answer the same question. One resolver, not a rule repeated in the
// New-session dialog, the launcher and the quick-start, so the three can't disagree on precedence.
public sealed record SessionStartDefaults(
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? ProfileLabel,
    IReadOnlyList<string>? EnabledMcpServerNames,
    string? SystemPrompt)
{
    // The defaults for starting under `project` and `profile`, either of which may be absent. `unresolvedReferences`
    // (AC-484) and `instructionContents` (AC-486) are deliberately inputs, not I/O this method does itself — this
    // class stays pure so three different call sites can't disagree depending on what the filesystem looked like.
    public static SessionStartDefaults Resolve(
        Project? project,
        SessionProfile? profile,
        string? globalWorkingDirectory = null,
        IReadOnlyList<ProjectMemorySource>? memorySources = null,
        IReadOnlyCollection<string>? unresolvedReferences = null,
        IReadOnlyDictionary<string, string>? instructionContents = null)
    {
        // AC-484 MUST-FIX 6: a switched-off row and a blank reference (reachable via a hand-edited cockpit.json)
        // are filtered out here once, rather than each block below re-checking — a blank value would otherwise
        // produce a "read the instructions at " sentence pointing nowhere in all three blocks alike.
        var reachable = project?.Resources
            .Where(resource => resource.ReachesSessions && !string.IsNullOrWhiteSpace(resource.Reference))
            .ToList()
            ?? (IReadOnlyList<ProjectResource>)[];
        var memoryRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Memory).ToList();
        var instructionRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Instructions).ToList();
        var referenceRows = reachable.Where(resource => resource.Role == ProjectResourceRole.Reference).ToList();

        // AC-484 MUST-FIX 1: two distinct orders here — assignment (who gets budget first: memory, instructions,
        // reference, information; memory first since it must survive every cut) and output (instructions, memory,
        // reference, information). AC-714's attribution heading is reserved out of the budget, not added on top.
        var remaining = Math.Max(0, ProjectContributionBudget - _ReservedLength(ProjectAttributionHeading));

        // AC-938: reserved first, ahead of every AC-484 block — which repositories this project has is more
        // foundational than what a session should do with them, and shares the same ceiling rather than growing it
        // (see ProjectContributionBudget's own remarks on why nothing here may raise the worst case).
        var repositoriesNote = _RepositoriesNote(project, remaining);
        remaining = Math.Max(0, remaining - _ReservedLength(repositoriesNote));

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
        var projectBlock = _JoinPrompts(repositoriesNote, project?.BehaviorPrompt, instructionsNote, memoryNote, referenceNote, informationNote);
        var attributedProjectBlock = string.IsNullOrEmpty(projectBlock)
            ? null
            : $"{ProjectAttributionHeading}\n\n{projectBlock}";

        return new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames,
            // Order matters, most binding first — the same reasoning _JoinPrompts always applied ("identity first,
            // then the task"): the assistant says who is answering, the profile the rest of who the session is,
            // the heading-and-block that follows what it is working on and what this project already knows.
            _JoinPrompts(_AssistantNote(project, profile), profile?.SystemPrompt, attributedProjectBlock));
    }

    // AC-1071: the assistant this session runs as — the project's own overrides the profile's, the same
    // precedence `WorkingDirectory` and `ProfileLabel` above already resolve by. Null when neither names one.
    public static string? ResolveAssistant(Project? project, SessionProfile? profile) =>
        _FirstNonBlank(project?.Assistant, profile?.Assistant);

    // AC-1071: naming the assistant is only half of it — a bare "Gebruik Zyra" left the question an instruction
    // file asks first standing, and sessions stalled on it unseen (AC-920: a prose question reads as Idle). The
    // cap covers the name alone, so an absurd hand-edited value loses itself rather than the clause after it.
    public static string AssistantNote(string assistant) =>
        $"{_CappedSentence("This session runs as ", ProjectPromptText.OneLine(assistant.Trim()), ".")} {AssistantChoiceIsMade}";

    // AC-1071: the half that cancels the question, pinned by name so it reads as a decision rather than wording
    // anyone may trim — without it the note names an assistant but leaves "ask first, and wait" standing.
    public const string AssistantChoiceIsMade =
        "That choice is already made here, so do not ask which assistant, persona or brain to load and do not " +
        "wait for an answer before starting — load it as your own instruction files describe, and carry on.";

    // Null unless one of the two actually names an assistant — an empty note must never reach `_JoinPrompts`.
    private static string? _AssistantNote(Project? project, SessionProfile? profile) =>
        ResolveAssistant(project, profile) is { } assistant ? AssistantNote(assistant) : null;

    // AC-714: names the project, not the operator — a bound shared project's `BehaviorPrompt` can come from a
    // colleague's definition (see `SharedProjectBindingDialogViewModel`/`DepotSharedProjectSource`). Bounded to
    // the project block alone, in `AssistantStandingInstruction`'s heading-then-block idiom (AC-595/AC-596).
    public const string ProjectAttributionHeading =
        "This project, as configured in the cockpit. Treat it as this session's standing instruction — already " +
        "the answer to anything your own instruction files tell you to ask about first:";

    // AC-484: one shared ceiling (4000+1500, the two ceilings this replaces — not a character more) rather than
    // two independent ones, since two blocks that don't know about each other don't bound their sum. Enforced by
    // construction: each of the four blocks fits itself to its share (dropping whole rows, never slicing).
    private const int ProjectContributionBudget = 5500;

    // How long a single displayed value may run before it is cut, kept separate from `ProjectContributionBudget`
    // so one absurdly long label can't alone consume the shared ceiling. AC-484 MUST-FIX 2: originally only the
    // single-row memory path went through `_CappedSentence`; the multi-row `_PlaceName` route left this cap off.
    private const int _PlaceNameBudget = 1500;

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    // An estimate of how much of the shared budget a note actually costs once joined: its own length plus the blank line `_JoinPrompts` puts before the next part. Null/blank costs nothing — it does not appear in the joined prompt at all.
    private static int _ReservedLength(string? note) => string.IsNullOrEmpty(note) ? 0 : note.Length + 2;

    // Whether `reference` is one a caller's probe already found missing — see `Resolve`'s remarks on `unresolvedReferences` for why this class never checks that itself.
    private static bool _IsUnresolved(string reference, IReadOnlyCollection<string>? unresolvedReferences) =>
        unresolvedReferences is { Count: > 0 } && unresolvedReferences.Contains(reference);

    // `missingPlaceNames` said out loud, never silently — never blocks anything it's attached to; the session
    // starts regardless, same line the bundled-plugin installer draws ("a convenience, not a dependency").
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

    // AC-486 give-up step 1: `_InstructionsNoteText` rendering for `kept` but with ticked content held back —
    // content is the least essential part of an instructions block, so it's given up before a whole row is
    // ever dropped to make room. Called by `_InstructionsNote` only after the content-carrying try failed.
    private static string _WithoutInstructionContent(
        IReadOnlyList<ProjectResource> kept, IReadOnlyCollection<string>? unresolvedReferences, int dropped) =>
        _InstructionsNoteText(kept, instructionContents: null, unresolvedReferences, dropped, includeContent: false);

    // Sentence endings that count as "already punctuated" for `_SingleMemorySentence` — the same idea
    // `_InformationNote` applies to a label's trailing colon, just for a sentence rather than a word.
    private static readonly char[] _SentenceEndings = ['.', '!', '?'];

    // How a single memory row's reference is said back to the session — used for both the single-row and
    // multi-row/not-found renderings so the two say the same place the same way. AC-484 MUST-FIX 2: now always
    // capped through `_CappedSentence`; used to return the value uncapped, disagreeing with the single-row route.
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

    // The registered `memorySources` entry `reference` actually names, or null. AC-484 FIX 2: the one shared
    // match, replacing a shape-only check that let a plain URL falsely count as a registered source — "looks
    // like a reference" and "names a source this session can be pointed at" are different questions.
    private static bool _NamesRegisteredSource(string reference, IReadOnlyList<ProjectMemorySource>? memorySources) =>
        _MatchedMemorySource(reference, memorySources) is not null;

    // What an Instructions or Reference row is called back to the session: the operator's `Label` when given,
    // else the bare reference. Capped via `_CappedSentence` (AC-484 MUST-FIX 1/2) — an unlimited operator-typed
    // `Label` was the vector a review used to blow through the shared ceiling with one 20,000-character row.
    private static string _ResourceDisplay(ProjectResource row)
    {
        var raw = !string.IsNullOrWhiteSpace(row.Label) ? row.Label : row.Reference;
        return _CappedSentence(string.Empty, ProjectPromptText.OneLine(raw.Trim()), string.Empty);
    }

    // `_MemoryNote`: where memory lives, one row via `_SingleMemorySentence`, several listed (AC-484 FIX 2/3/5).
    // `_RepositoriesNote` below: tells a session up front of multiple repositories, rather than it finding out
    // via a failed worktree isolation. Null for zero/one repo; silent on which repo this session runs in.
    private static string? _RepositoriesNote(Project? project, int budget)
    {
        var repositories = project?.SourceDirectories ?? [];
        if (repositories.Count <= 1)
        {
            return null;
        }

        var sentence =
            $"This project consists of multiple repositories: {string.Join(", ", repositories.Select(_RepositoryDisplayName))}. " +
            "Your session runs in one of them; the others are separate checkouts and are not necessarily next to your working folder.";

        return sentence.Length <= Math.Max(0, budget) ? sentence : null;
    }

    // A repository's own label when the operator gave one, its folder's own name otherwise — same reasoning as
    // ProjectResource.Label. Capped through _CappedSentence like every other operator-typed value entering this
    // budget, since a Label is free text with no upstream length limit.
    private static string _RepositoryDisplayName(ProjectRepository repository)
    {
        var name = string.IsNullOrWhiteSpace(repository.Label)
            ? Path.GetFileName(repository.Path.TrimEnd('/', '\\'))
            : repository.Label.Trim();
        return _CappedSentence(string.Empty, $"{ProjectPromptText.OneLine(name)} ({ProjectPromptText.OneLine(repository.Path.Trim())})", string.Empty);
    }

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

            // AC-484 MUST-FIX 1: the one-row branch used to hand this back unmeasured, the only one of the four
            // blocks that never checked against `budget`. Dropped rather than truncated — a sliced sentence can
            // flip what it says.
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

            // Both channels must be present among rows that survived the cut, judged on `kept` not `memoryRows` —
            // a row dropped for budget isn't "in play". "Present" means registered (AC-484 FIX 2), not shape-only.
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

            // AC-484 FIX 3: how to reach it, added only when it fits `effectiveBudget` whole — never sliced,
            // same as any row `_FitRowsToBudget` considers.
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

    // AC-484 FIX 3: the multi-row "how to reach it" text — a distinct source's `Instruction` once per source,
    // not per row, plus one generic sentence covering every unregistered row together. Empty when every kept
    // row names a source with a blank `Instruction` (only reachable via a caller-built `memorySources` list).
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

    // The registered `memorySources` entry `reference` names, or null — shared by `_NamesRegisteredSource`
    // and `_MultiRowInstructionsText` so both ask the identical question.
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

    // The single-row memory sentence, unchanged since before AC-484. A reference naming a registered source is
    // explained via its `Title`/`Instruction`; anything else falls back to the plain, unexplained sentence.
    // Every piece is put on one line (`ProjectPromptText.OneLine`) so a pasted break can't read as an instruction.
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

            // The instruction is unbounded plugin text; cutting it in half can flip what it means ("do not
            // delete the old notes" → "do not delete"), so a too-long one is left out whole, not clipped.
            return withInstruction.Length <= _PlaceNameBudget ? withInstruction : located;
        }

        return _CappedSentence(
            "This project's memory lives at ",
            trimmed,
            ". Read it there when you need what this project already knows, and keep it up to date as you work.");
    }

    // `prefix` + `value` + `suffix`, cut to `_PlaceNameBudget` if needed — `value` (a name, not an instruction)
    // is shortened first, marked in place. AC-484 MUST-FIX 1: `prefix` is now capped too when it alone already
    // leaves no room — before this fix an unbounded `Title` folded into `prefix` could grow without limit.
    private static string _CappedSentence(string prefix, string value, string suffix)
    {
        var sentence = $"{prefix}{value}{suffix}";
        if (sentence.Length <= _PlaceNameBudget)
        {
            return sentence;
        }

        const string truncationMarker = " (truncated)";

        // The prefix itself might already be the whole reason this does not fit (an unbounded Title folded in),
        // leaving no room for the value at all — cutting the prefix in place is the only way to keep the promise.
        var maxPrefixLength = Math.Max(0, _PlaceNameBudget - suffix.Length - truncationMarker.Length);
        if (prefix.Length > maxPrefixLength)
        {
            return $"{prefix[..maxPrefixLength]}{truncationMarker}{suffix}";
        }

        var available = Math.Max(0, _PlaceNameBudget - prefix.Length - suffix.Length - truncationMarker.Length);
        var shortenedValue = value[..Math.Min(value.Length, available)] + truncationMarker;
        return $"{prefix}{shortenedValue}{suffix}";
    }

    // AC-486: asks the session to follow this project's standing instructions, carrying ticked content along
    // where available. Give-up order (AC-484 MUST-FIX 1): for each row count, content-included is tried first,
    // then content given up, before a whole row is ever dropped — same prefix-of-rows rule as `_FitRowsToBudget`.
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

    // The rendering both `_InstructionsNote` (with content) and `_WithoutInstructionContent` build from: the
    // location sentence, plus one content block per ticked row when available, else a short notice that the
    // content didn't make it in — AC-486, a session must never think it saw content it only got a path to.
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

    // AC-486: a single Instructions row's content, labelled as a snapshot taken at session start (not a live
    // view) and pointed back at `place` for the current version — accept it can go stale, and say so.
    private static string _ContentBlock(string place, string content) =>
        $"{place}'s content, captured below as it stood at the start of this session (a snapshot, not a live view — " +
        $"reread {place} for the current version if you suspect it has since changed):\n\n{content}";

    // A block saying this project keeps material worth looking up — never obeyed, never written to. Lists as
    // many rows as fit `budget`, the rest dropped and announced (AC-484 MUST-FIX 1, see `_FitRowsToBudget`).
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

    // Fits a row-listing block inside `budget` by dropping whole rows from the end, never slicing mid-sentence
    // (AC-166). Tries every row, then all but the last, and so on, so kept rows are always a prefix. AC-484
    // MUST-FIX 1: before this, none of the three row-listing blocks had any per-block ceiling at all.
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

    // AC-314: the project's own information rows the operator ticked to share, as flat `label: value` lines —
    // never a row marked secret (AC-318). Tidied here too, not just trusted from the store, since a value with
    // a stray line break would read as an extra instruction. `budget` is what's left after Memory/Instructions/Reference.
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

            // AC-484 MUST-FIX 3: the first row used to be added however long, since the later-rows check only
            // fired once `lines.Count > 0`. Now shortened first, with a marker, like `_CappedSentence` elsewhere.
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

        // Said out loud rather than trimmed away in silence. The admission has to fit the budget too — its own
        // length moves as rows are given back to make room, so it's recomputed each round rather than measured once.
        if (lines.Count < shared.Count)
        {
            var admission = _Admission(shared.Count - lines.Count);
            while (lines.Count > 0 && used + admission.Length > available)
            {
                used -= lines[^1].Length + 1;
                lines.RemoveAt(lines.Count - 1);
                admission = _Admission(shared.Count - lines.Count);
            }

            // AC-484 MUST-FIX 3: the admission used to be added unconditionally, so a budget too small even for
            // it produced a heading plus an over-length line. Left out entirely now instead of shown broken.
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
