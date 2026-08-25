namespace Cockpit.Core.Worktrees;

// Where the cockpit creates the git worktrees that isolate sessions (AC-85). `Root` null or blank keeps
// the default `worktrees/` folder under the app state root; an operator can override it, e.g. for more room.
// Existing worktrees keep the absolute path they were made at, so changing this never strands them.
public sealed record WorktreeSettings
{
    public string? Root { get; init; }
}
