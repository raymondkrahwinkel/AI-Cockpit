namespace Cockpit.Plugins.Abstractions.Workspaces;

/// <summary>
/// A git worktree the host created for a plugin (AC-174, Raymond 2026-07-22) — its on-disk <see cref="Path"/> and the
/// <see cref="Branch"/> it is on. An Autopilot run creates one per run and hands its <see cref="Path"/> to each step's
/// <see cref="EmbeddedSessionRequest.WorktreePath"/>, so every step works in it and their changes accumulate on the one
/// <see cref="Branch"/> — the merge-ready result the operator reviews. The worktree persists after the run (it is the
/// deliverable), managed from the cockpit's Worktrees panel like any other.
/// </summary>
public sealed record PluginWorktreeInfo(string Path, string Branch);
