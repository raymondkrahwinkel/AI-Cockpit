using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Worktrees;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One worktree in the management panel (AC-85): its git state (clean/dirty/ahead) and whether the session that
/// owns it is still alive, so it is never a guess whether removing it loses work, and reattach is only offered when
/// there is no live session already on the tree.
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

    /// <summary>True while a session is still running on this worktree — reattach is blocked, removing it would pull the tree from under it.</summary>
    public bool IsOwnerLive { get; }

    public string RepositoryName => System.IO.Path.GetFileName(Record.RepositoryRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public string Branch => Record.Branch;

    public string WorktreePath => Record.Path;

    public bool IsClean => Status.IsClean;

    /// <summary>Reattach is offered only when the owning session is gone (Raymond 2026-07-19: GONE only) — never onto a live tree.</summary>
    public bool CanReattach => !IsOwnerLive;

    /// <summary>A plain-language state for the pill, in the order that matters for data safety: gone folder, then unsaved work, then unmerged commits, then retained, then clean.</summary>
    public string StatusLabel =>
        !Status.Exists ? "Folder missing"
        : Status.HasUncommittedChanges ? "Uncommitted changes"
        : Status.CommitsAhead > 0 ? $"{Status.CommitsAhead} commit(s) ahead"
        : Record.IsRetained ? "Retained"
        : "Clean";

    public string StatusBrushKey =>
        !Status.Exists ? "CockpitTextFaintBrush"
        : Status.HasUncommittedChanges ? "CockpitStatusWaitingBrush"
        : Status.CommitsAhead > 0 ? "CockpitStatusBusyBrush"
        : "CockpitStatusDoneBrush";

    public string OwnerLabel => IsOwnerLive ? "in use · live session" : "session gone";

    public string OwnerBrushKey => IsOwnerLive ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";
}
