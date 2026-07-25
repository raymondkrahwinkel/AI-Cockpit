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
    /// <remarks>
    /// The MCP selection here stays the profile's, and that is not a gap in "the project wins": a project's
    /// selection is a per-server answer rather than a list (<see cref="ProjectMcpOverlay.IsSelectedByDefault"/>),
    /// applied where the checklist is built, and it beats this one wherever a project is in play. Resolving it into
    /// a list here would need the catalog — which this rule deliberately knows nothing about.
    /// </remarks>
    public static SessionStartDefaults Resolve(
        Project? project,
        SessionProfile? profile,
        string? globalWorkingDirectory = null) =>
        new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames,
            _JoinPrompts(profile?.SystemPrompt, project?.BehaviorPrompt, _MemoryNote(project)));

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));

    /// <summary>
    /// Where the project keeps its memory, said in a sentence the session can act on. Null for a project without
    /// one. Deliberately told rather than loaded: the host does not know what lives there — a folder of notes, a
    /// Depot project (AC-165/166) — and a session that is told where to look can go and look.
    /// </summary>
    private static string? _MemoryNote(Project? project) =>
        project?.MemoryRef is { Length: > 0 } memory && !string.IsNullOrWhiteSpace(memory)
            ? $"This project's memory lives at {memory.Trim()}. Read it there when you need what this project already knows, and keep it up to date as you work."
            : null;

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
