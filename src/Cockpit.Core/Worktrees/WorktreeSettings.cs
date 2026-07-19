namespace Cockpit.Core.Worktrees;

/// <summary>
/// Where the cockpit creates the git worktrees that isolate sessions (AC-85). <see cref="Root"/> null or blank keeps
/// the default — a <c>worktrees/</c> folder under the app state root — while an operator can override it, for example
/// to a faster disk or one with more room; new worktrees then go there. Existing worktrees keep the absolute path
/// they were made at, so changing this never strands them.
/// </summary>
public sealed record WorktreeSettings
{
    public string? Root { get; init; }
}
