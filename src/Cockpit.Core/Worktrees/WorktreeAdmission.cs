using Cockpit.Core.Abstractions.Worktrees;

namespace Cockpit.Core.Worktrees;

// Where a starting session may run, and on which branch (AC-85, AC-938). Decision set: nothing was admitted.
public sealed record AdmittedDirectory(string? WorkingDirectory, string? WorktreeBranch, NeedsWorktreeDecision? Decision = null);

// AC-1448: the one admission rule, run by the desktop's start and by the backend `SessionLauncher` alike. A worktree
// owned by a live session throws `WorktreeAdmissionException`; whether the owner counts as live (the assistant's own
// worktrees do not, AC-719) is the manager's lease (AC-1098), not this rule's.
public static class WorktreeAdmission
{
    public static async Task<AdmittedDirectory> AdmitAsync(
        IWorktreeManager? worktrees, string paneId, string? sessionLabel, string? workingDirectory, bool isolate,
        CancellationToken cancellationToken = default)
    {
        if (!isolate && !string.IsNullOrWhiteSpace(workingDirectory))
        {
            if (await MatchingAsync(worktrees, workingDirectory, cancellationToken).ConfigureAwait(false) is not { } managed)
            {
                return new AdmittedDirectory(workingDirectory, null);
            }

            if (worktrees is null || await worktrees.ReattachAsync(managed.Path, paneId, cancellationToken).ConfigureAwait(false) is not { } reattached)
            {
                throw new WorktreeAdmissionException(managed.Path, managed.SessionId);
            }

            return new AdmittedDirectory(reattached.Path, reattached.Branch);
        }

        if (!isolate)
        {
            return new AdmittedDirectory(workingDirectory, null);
        }

        try
        {
            if (worktrees is null)
            {
                throw new InvalidOperationException("worktree isolation is not available here (no worktree manager).");
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new InvalidOperationException("no working directory is set, so no isolated worktree can be created.");
            }

            // Reattach: the folder is already a worktree the cockpit created — re-own it for this session and run
            // there, rather than nesting a new worktree inside it.
            if (await MatchingAsync(worktrees, workingDirectory, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (await worktrees.ReattachAsync(existing.Path, paneId, cancellationToken).ConfigureAwait(false) is { } reattached)
                {
                    return new AdmittedDirectory(reattached.Path, reattached.Branch);
                }

                throw new WorktreeAdmissionException(existing.Path, existing.SessionId);
            }

            if (await worktrees.DetectRepositoryAsync(workingDirectory, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("the working directory is not a git repository, so no isolated worktree can be created.");
            }

            var worktree = await worktrees.CreateForSessionAsync(paneId, sessionLabel, workingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AdmittedDirectory(worktree.Path, worktree.Branch);
        }
        catch (WorktreeAdmissionException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Never a silent fallback to the shared checkout: that is the contamination isolation exists to prevent.
            return new AdmittedDirectory(null, null, new NeedsWorktreeDecision(workingDirectory, exception.Message));
        }
    }

    // The registered worktree occupying exactly `workingDirectory`, by the OS-aware comparison the engine uses (AC-320).
    public static async Task<WorktreeRecord?> MatchingAsync(
        IWorktreeManager? worktrees, string workingDirectory, CancellationToken cancellationToken = default) =>
        worktrees is null
            ? null
            : WorktreeLookup.At(await worktrees.ListAsync(cancellationToken).ConfigureAwait(false), workingDirectory);
}
