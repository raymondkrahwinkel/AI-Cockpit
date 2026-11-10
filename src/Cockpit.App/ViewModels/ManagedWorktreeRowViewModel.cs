using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Worktrees;

namespace Cockpit.App.ViewModels;

// One worktree in the management panel (AC-85): its git state (clean/dirty/ahead) and whether the pane that owns
// it still exists, so it is never a guess whether removing it loses work, and reattach is only offered when there
// is no owning pane already on the tree.
public sealed partial class ManagedWorktreeRowViewModel : ObservableObject
{
    // The owner's display name, sanitized — null when none was supplied or nothing survived sanitizing.
    private readonly string? _ownerName;
    private readonly WorktreeStatus? _status;

    public ManagedWorktreeRowViewModel(WorktreeStatus status, bool isOwnerLive, string? ownerName = null, bool hasOpenRestoreOffer = false)
    {
        _status = status;
        IsOwnerLive = isOwnerLive;
        HasOpenRestoreOffer = hasOpenRestoreOffer;
        _ownerName = _SanitizeOwnerName(ownerName);
    }

    internal ManagedWorktreeRowViewModel(RemoteManagedWorktree worktree)
    {
        Remote = worktree;
        IsOwnerLive = worktree.IsOwnerLive;
    }

    internal RemoteManagedWorktree? Remote { get; }

    public bool IsRemote => Remote is not null;

    public bool CanOpenFolder => !IsRemote;

    public string OpenFolderHint => IsRemote ? "Server files are available in Files; this folder is not on this computer." : "Open folder — open the worktree folder in your file manager";

    public string ReattachHint => IsRemote ? "This server does not offer worktree reattachment in this window yet." : "Reattach — start a new session in this worktree. Only for a worktree whose session is gone.";

    public WorktreeStatus Status => _status ?? throw new InvalidOperationException("A server worktree has no local registry record.");

    public WorktreeRecord Record => Status.Record;

    // Not necessarily a running session (AC-410): a restored, not-yet-started pane is "live" here too, on purpose — its
    // worktree must stay reserved for the resume offer it is still showing, even though nothing on it is actually
    // running yet.
    public bool IsOwnerLive { get; }

    // Whether the owning pane currently shows an open restore offer (AC-410) — the reason `IsOwnerLive`
    // can be true with nothing actually running behind it. Restore starts a session and clears the offer before
    // anything else, so the two never overlap: a truly running session reads false here.
    public bool HasOpenRestoreOffer { get; }

    // Whether "Release" should be offered (AC-520 fix 6): only when the owner counts as live purely on the strength
    // of a restore offer nobody has acted on. A session that is actually doing something disables it — Remove and
    // Reattach are already available the moment the offer is gone, so there is nothing left for Release to do.
    public bool CanRelease => IsOwnerLive && HasOpenRestoreOffer;

    public string RepositoryName => Remote?.ProjectName ?? System.IO.Path.GetFileName(Record.RepositoryRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public string Branch => Remote?.Branch ?? Record.Branch;

    public string WorktreePath => IsRemote ? "On the server" : Record.Path;

    public bool Exists => Remote?.Exists ?? Status.Exists;

    public bool HasUncommittedChanges => Remote?.HasUncommittedChanges ?? Status.HasUncommittedChanges;

    public int StrandableCommits => Remote?.StrandableCommits ?? Status.StrandableCommits;

    public bool NothingToKeep => Remote is { } remote ? !remote.Exists : Status.NothingToKeep;

    public bool IsClean => !NothingToKeep && !HasUncommittedChanges && StrandableCommits == 0;

    public string? RetentionReason => IsRemote ? null : Record.RetentionReason;

    // Reattach is offered only when the owning session is gone (Raymond 2026-07-19: GONE only) — never onto a live
    // tree.
    public bool CanReattach => !IsRemote && !IsOwnerLive;

    // Remove is blocked while a session is still on the tree (Raymond 2026-07-19): removing it would pull the working
    // directory out from under a running session.
    public bool CanRemove => !IsOwnerLive;

    // A plain-language state for the pill, in the order that matters for data safety: gone folder, then an emptied one,
    // then unsaved work, then commits that exist nowhere else, then retained, then clean.
    public string StatusLabel =>
        !Exists ? "Folder missing"
        : !IsRemote && Status.WorkingCopyMissing ? "No working copy"
        : HasUncommittedChanges ? "Uncommitted changes"
        : StrandableCommits > 0 ? $"{StrandableCommits} commit(s) only here"
        : !IsRemote && Record.IsRetained ? "Retained"
        : "Clean";

    public string StatusBrushKey =>
        NothingToKeep ? "CockpitTextFaintBrush"
        : HasUncommittedChanges ? "CockpitStatusWaitingBrush"
        : StrandableCommits > 0 ? "CockpitStatusBusyBrush"
        : "CockpitStatusDoneBrush";

    // "live session" would overclaim for a restored, not-yet-started pane (AC-410): its worktree is protected the
    // same way, but nothing on it is actually running. "in use" (below) is true in both cases; naming the owner,
    // where known, does not change that — it says whose pane holds the tree, not that the pane is doing anything.
    public string OwnerLabel => (IsOwnerLive, _ownerName) switch
    {
        (true, { } name) => $"in use · claimed by {name}",
        (false, { } name) => $"session gone · was {name}",
        (true, null) => "in use · claimed by a pane",
        (false, null) => "session gone",
    };

    public string OwnerBrushKey => IsOwnerLive ? "CockpitStatusBusyBrush" : "CockpitTextFaintBrush";

    // Control characters — and the Unicode line/paragraph separators git's own ref-name check does not reject (same
    // reasoning as WorktreeTools._SingleLine) — are folded to a space so a name can never break the status row onto
    // more than one line (AC-310).
    private const int MaxOwnerNameLength = 40;

    private static string? _SanitizeOwnerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var flattened = new string(name
            .Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' or '\u0085' ? ' ' : character)
            .ToArray()).Trim();

        if (flattened.Length == 0)
        {
            return null;
        }

        return flattened.Length > MaxOwnerNameLength
            ? string.Concat(flattened.AsSpan(0, MaxOwnerNameLength).TrimEnd(), "…")
            : flattened;
    }
}
