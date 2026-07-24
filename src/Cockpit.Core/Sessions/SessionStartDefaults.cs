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
/// <param name="EnabledMcpServerNames">Which servers open ticked; null means no restriction (every offered server).</param>
public sealed record SessionStartDefaults(
    string? WorkingDirectory,
    bool IsolateInWorktree,
    string? ProfileLabel,
    IReadOnlyList<string>? EnabledMcpServerNames)
{
    /// <summary>
    /// The defaults for starting under <paramref name="project"/> and <paramref name="profile"/>, either of which
    /// may be absent — a session without a project is how the cockpit has always started one.
    /// </summary>
    /// <param name="globalWorkingDirectory">The configured app-wide working directory, used when neither the project nor the profile names one.</param>
    /// <remarks>
    /// The MCP selection is the profile's on purpose, and it is not a gap in "the project wins". The two answer
    /// different questions: the project's <see cref="ProjectMcpOverlay"/> decides which servers <em>exist</em> for
    /// its sessions, while this list decides which of the offered ones open <em>ticked</em>. A name the overlay
    /// removed simply is not offered, so the project still has the last word without owning a second list that
    /// could contradict the first.
    /// </remarks>
    public static SessionStartDefaults Resolve(
        Project? project,
        SessionProfile? profile,
        string? globalWorkingDirectory = null) =>
        new(
            _FirstNonBlank(project?.SourceDirectory, profile?.DefaultWorkingDirectory, globalWorkingDirectory),
            project?.IsolateInWorktreeByDefault ?? false,
            _FirstNonBlank(project?.DefaultProfileLabel, profile?.Label),
            profile?.EnabledMcpServerNames);

    private static string? _FirstNonBlank(params string?[] candidates) =>
        Array.Find(candidates, candidate => !string.IsNullOrWhiteSpace(candidate));
}
