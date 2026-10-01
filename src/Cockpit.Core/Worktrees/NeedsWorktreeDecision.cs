namespace Cockpit.Core.Worktrees;

// AC-1448: isolation was asked for and could not be had. Only a person may choose to run in `WorkingDirectory` anyway;
// nothing has started, and a caller without one to ask refuses with `Reason`.
public sealed record NeedsWorktreeDecision(string? WorkingDirectory, string Reason);
