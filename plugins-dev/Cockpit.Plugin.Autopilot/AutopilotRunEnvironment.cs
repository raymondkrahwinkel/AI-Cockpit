using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

/// <summary>
/// Where an Autopilot run works and whether its steps isolate (AC-174) — resolved once at the run's start and handed to
/// the coordinator so every step launches the same way.
/// <list type="bullet">
/// <item><see cref="RepositoryDirectory"/> — the folder the run works in (the operator's chosen directory).</item>
/// <item><see cref="RunWorktreePath"/> — the run's shared worktree for a single-agent step, or null (a parallel step
/// gets a fresh worktree per agent, and a run that does not isolate has none).</item>
/// <item><see cref="RunWorktreeBranch"/> — the branch that shared worktree is on (AC-216), the branch a merge-ready code
/// run pushes and opens its PR from; null when there is no run worktree.</item>
/// <item><see cref="IsolateSteps"/> — whether each step runs isolated in a worktree. True for a git repository (the
/// fail-closed default); false only when the host positively reported the folder is not a git repository, so an admin
/// task in a plain folder runs there directly instead of being refused for "no git repository".</item>
/// </list>
/// </summary>
internal sealed record AutopilotRunEnvironment(string RepositoryDirectory, string? RunWorktreePath, bool IsolateSteps, string? RunWorktreeBranch = null)
{
    /// <summary>Whether this run has one shared git worktree on its own branch — the merge-ready deliverable a code run can push and open a PR from (AC-216). False for a parallel-only or non-git run.</summary>
    public bool HasRunBranch => RunWorktreePath is { Length: > 0 } && RunWorktreeBranch is { Length: > 0 };

    /// <summary>
    /// Whether a run in a folder with the given git status isolates its steps (AC-174, Raymond 2026-07-22) — the
    /// fail-closed rule, in one place so it is testable and cannot drift. Isolate unless the host <em>positively</em>
    /// reported the folder is not a git repository: <see cref="GitDirectoryStatus.Unknown"/> (an older host, a failed
    /// probe) stays isolated, so the confinement guard is never dropped by an inconclusive answer. Only
    /// <see cref="GitDirectoryStatus.NotARepository"/> — a plain folder, an admin task with no repo — runs without it.
    /// </summary>
    public static bool IsolateFor(GitDirectoryStatus status) => status != GitDirectoryStatus.NotARepository;
}
