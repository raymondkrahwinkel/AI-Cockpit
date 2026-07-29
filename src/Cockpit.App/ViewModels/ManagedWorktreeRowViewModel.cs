using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Worktrees;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One worktree in the management panel (AC-85): its git state (clean/dirty/ahead) and whether the pane that owns
/// it still exists, so it is never a guess whether removing it loses work, and reattach is only offered when there
/// is no owning pane already on the tree.
/// </summary>
public sealed partial class ManagedWorktreeRowViewModel : ObservableObject
{
    public ManagedWorktreeRowViewModel(WorktreeStatus status, bool isOwnerLive)
    {
        Status = status;
        IsOwnerLive = isOwnerLive;
    }

    public WorktreeStatus Status { get; }

    public WorktreeRecord Record => Status.Record;

    /// <summary>
    /// True while the pane that owns this worktree still exists — reattach is blocked, removing it would pull the
    /// tree out from under it. Not necessarily a running session (AC-410): a restored, not-yet-started pane is
    /// "live" here too, on purpose — its worktree must stay reserved for the resume offer it is still showing,
    /// even though nothing on it is actually running yet.
    /// </summary>
    public bool IsOwnerLive { get; }

    public string RepositoryName => System.IO.Path.GetFileName(Record.RepositoryRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public string Branch => Record.Branch;

    public string WorktreePath => Record.Path;

    public bool IsClean => Status.IsClean;

    /// <summary>Reattach is offered only when the owning session is gone (Raymond 2026-07-19: GONE only) — never onto a live tree.</summary>
    public bool CanReattach => !IsOwnerLive;

    /// <summary>Remove is blocked while a session is still on the tree (Raymond 2026-07-19): removing it would pull the working directory out from under a running session. Close the session first.</summary>
    public bool CanRemove => !IsOwnerLive;

    /// <summary>A plain-language state for the pill, in the order that matters for data safety: gone folder, then an emptied one, then unsaved work, then commits that exist nowhere else, then retained, then clean.</summary>
    public string StatusLabel =>
        !Status.Exists ? "Folder missing"
        : Status.WorkingCopyMissing ? "No working copy"
        : Status.HasUncommittedChanges ? "Uncommitted changes"
        : Status.StrandableCommits > 0 ? $"{Status.StrandableCommits} commit(s) only here"
        : Record.IsRetained ? "Retained"
        : "Clean";

    public string StatusBrushKey =>
        Status.NothingToKeep ? "CockpitTextFaintBrush"
        : Status.HasUncommittedChanges ? "CockpitStatusWaitingBrush"
        : Status.StrandableCommits > 0 ? "CockpitStatusBusyBrush"
        : "CockpitStatusDoneBrush";

    // "live session" would overclaim for a restored, not-yet-started pane (AC-410): its worktree is protected the
    // same way, but nothing on it is actually running. "claimed by a pane" is true in both cases.
    public string OwnerLabel => IsOwnerLive ? "in use · claimed by a pane" : "session gone";

    public string OwnerBrushKey => IsOwnerLive ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";
}
