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
/// (AC-142), then what the project asks of it. Null when neither has anything to say.
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
    /// project's <see cref="Project.MemoryRef"/> naming one of them is explained rather than merely quoted. Null (the
    /// default) is exactly "none registered" — every caller that does not yet pass this list gets the plain,
    /// unexplained sentence it always got, unchanged.
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
        IReadOnlyList<ProjectMemorySource>? memorySources = null) =>
        new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames,
            _JoinPrompts(profile?.SystemPrompt, project?.BehaviorPrompt, _MemoryNote(project, memorySources), _InformationNote(project)));

    /// <summary>
    /// How much of the standing instructions a project's shared information rows may take. A ceiling rather than trust,
    /// because this block is the one part of the prompt that grows by the row: the Claude route hands the whole prompt
    /// to its CLI as one argument, and a process command line has a hard limit — so an unbounded block does not merely
    /// cost budget, it stops the session starting at all. Generous enough that a realistic project never meets it.
    /// </summary>
    private const int InformationNoteBudget = 4000;

    /// <summary>
    /// How much of the standing instructions a memory note may take. A ceiling for the same reason
    /// <see cref="InformationNoteBudget"/> is one: the Claude route hands the whole prompt to its CLI as one process
    /// argument, and a command line has a hard limit, so an unbounded sentence does not merely cost budget, it stops
    /// the session starting at all. Far smaller than the information block's budget because this is one sentence, not
    /// a list that grows by the row — a realistic memory location is a path or a project key, at most a few hundred
    /// characters, and a realistic source instruction is a sentence or two a plugin author wrote by hand. 1500 leaves
    /// room for both together several times over while still refusing to let either grow without limit.
    /// </summary>
    private const int MemoryNoteBudget = 1500;

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    /// <summary>
    /// Sentence endings that count as "already punctuated" for <see cref="_MemoryNote"/> — the same idea
    /// <see cref="_InformationNote"/> applies to a label's trailing colon, just for a sentence rather than a word.
    /// </summary>
    private static readonly char[] _SentenceEndings = ['.', '!', '?'];

    /// <summary>
    /// Where the project keeps its memory, said in a sentence the session can act on. Null for a project without
    /// one.
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
    private static string? _MemoryNote(Project? project, IReadOnlyList<ProjectMemorySource>? memorySources)
    {
        if (project?.MemoryRef is not { Length: > 0 } memory || string.IsNullOrWhiteSpace(memory))
        {
            return null;
        }

        var trimmed = ProjectPromptText.OneLine(memory.Trim());
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
            return withInstruction.Length <= MemoryNoteBudget ? withInstruction : located;
        }

        return _CappedSentence(
            "This project's memory lives at ",
            trimmed,
            ". Read it there when you need what this project already knows, and keep it up to date as you work.");
    }

    /// <summary>
    /// <paramref name="prefix"/> + <paramref name="value"/> + <paramref name="suffix"/>, cut down to
    /// <see cref="MemoryNoteBudget"/> if it does not fit. Only <paramref name="value"/> is ever shortened: it is a
    /// name — a path, a project key — not an instruction, so unlike an instruction it is safe to cut rather than
    /// having to be dropped whole. The cut is marked in place, in the spirit of <see cref="_InformationNote"/>'s
    /// "(and N more that did not fit here…)": the session, and the operator reading the prompt back, should be able
    /// to see that the location shown is not the whole of it.
    /// </summary>
    private static string _CappedSentence(string prefix, string value, string suffix)
    {
        var sentence = $"{prefix}{value}{suffix}";
        if (sentence.Length <= MemoryNoteBudget)
        {
            return sentence;
        }

        const string truncationMarker = " (truncated)";
        var available = Math.Max(0, MemoryNoteBudget - prefix.Length - suffix.Length - truncationMarker.Length);
        var shortenedValue = value[..Math.Min(value.Length, available)] + truncationMarker;
        return $"{prefix}{shortenedValue}{suffix}";
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
    private static string? _InformationNote(Project? project)
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
        var budget = InformationNoteBudget;
        foreach (var field in shared)
        {
            // A label the operator already punctuated keeps its own colon rather than getting a second one.
            var label = field.Label.EndsWith(':') ? field.Label : $"{field.Label}:";
            var line = field.HasLabel ? $"- {label} {field.Value}" : $"- {field.Value}";
            if (line.Length > budget && lines.Count > 0)
            {
                break;
            }

            lines.Add(line);
            budget -= line.Length + 1;
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
