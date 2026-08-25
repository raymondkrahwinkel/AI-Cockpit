using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// Where an Autopilot run works and whether its steps isolate (AC-174), resolved once at the run's start and handed
// to the coordinator. IsolateSteps is true for a git repository (fail-closed default), false only when the host
// positively reported it is not one; RunId/RunLabel let the host add up cost totals per run (AC-251).
internal sealed record AutopilotRunEnvironment(
    string RepositoryDirectory,
    string? RunWorktreePath,
    bool IsolateSteps,
    string? RunWorktreeBranch = null,
    string? RunId = null,
    string? RunLabel = null)
{
    // Whether this run has one shared git worktree on its own branch — the merge-ready deliverable a code run can push and open a PR from (AC-216). False for a parallel-only or non-git run.
    public bool HasRunBranch => RunWorktreePath is { Length: > 0 } && RunWorktreeBranch is { Length: > 0 };

    // Whether a run in a folder with the given git status isolates its steps (AC-174) — the fail-closed rule, in
    // one place so it is testable. Isolate unless the host *positively* reported NotARepository; Unknown (older
    // host, failed probe) stays isolated so the guard is never dropped by an inconclusive answer.
    public static bool IsolateFor(GitDirectoryStatus status) => status != GitDirectoryStatus.NotARepository;
}
