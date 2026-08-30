namespace Cockpit.Core.Worktrees;

// Signals a live writer so interactive and headless callers can refuse without a dialog.
public sealed class WorktreeAdmissionException(string worktreePath, string ownerSessionId)
    : InvalidOperationException($"Cannot start in '{worktreePath}' because live Cockpit session '{ownerSessionId}' already owns that worktree.")
{
    public string WorktreePath { get; } = worktreePath;

    public string OwnerSessionId { get; } = ownerSessionId;
}
