namespace Cockpit.Plugin.Autopilot;

// Where a worktree stood at one moment (AC-255) — what a step's own change is later measured against.
// Deliberately not just a commit: a mark that only remembered `HEAD` would carry every earlier step's
// uncommitted work into the next step's evidence.
internal sealed record AutopilotWorktreeMark(string Commit, IReadOnlyList<string> UntrackedFiles);
