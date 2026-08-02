using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.Autopilot;

// Where an Autopilot run works and whether its steps isolate (AC-174) — resolved once at the run's start and handed to
// the coordinator so every step launches the same way.
// - `RepositoryDirectory` — the folder the run works in (the operator's chosen directory).
// - `RunWorktreePath` — the run's shared worktree for a single-agent step, or null (a parallel step
// gets a fresh worktree per agent, and a run that does not isolate has none).
// - `RunWorktreeBranch` — the branch that shared worktree is on (AC-216), the branch a merge-ready code
// run pushes and opens its PR from; null when there is no run worktree.
// - `IsolateSteps` — whether each step runs isolated in a worktree. True for a git repository (the
// fail-closed default); false only when the host positively reported the folder is not a git repository, so an admin
// task in a plain folder runs there directly instead of being refused for "no git repository".
// - `RunId`/`RunLabel` — what the host records this run's sessions under so their token
// and cost totals can be added up per run afterwards (AC-251). Null in a test that does not care; a real run always
// carries one.
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

    // Whether a run in a folder with the given git status isolates its steps (AC-174) — the
    // fail-closed rule, in one place so it is testable and cannot drift. Isolate unless the host *positively*
    // reported the folder is not a git repository: `GitDirectoryStatus.Unknown` (an older host, a failed
    // probe) stays isolated, so the confinement guard is never dropped by an inconclusive answer. Only
    // `GitDirectoryStatus.NotARepository` — a plain folder, an admin task with no repo — runs without it.
    public static bool IsolateFor(GitDirectoryStatus status) => status != GitDirectoryStatus.NotARepository;
}
