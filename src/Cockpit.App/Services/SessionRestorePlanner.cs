using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// What a saved AI-session pane can be restored with (AC-410) — the answers `CockpitViewModel.RestoreSessionPanesAsync`
// needs before it materializes a panel, reached without starting anything. Modelled on `ProjectQuickStart`:
// this only answers "with what"; the cockpit's own launch path still owns actually starting a session.
public sealed class SessionRestorePlanner(ISessionProfileStore profiles) : ISingletonService
{
    // Builds the plan for one saved pane. `state` is the latest `SessionStateRecord` for
    // this pane's id, or null when none was ever written — a pane persisted right before a crash, before its
    // session got far enough to report anything. A profile that no longer exists degrades the plan rather than
    // throwing (matches `ProjectQuickStart.ComposeAsync`: a config a pane can no longer resolve is
    // not a crash).
    public async Task<SessionRestorePlan> ComposeAsync(WorkspacePane pane, SessionStateRecord? state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pane.ProfileId))
        {
            return new SessionRestorePlan(pane, null, SessionRestoreAvailability.Unknown, "This pane was saved without a profile.") { State = state };
        }

        var configured = await profiles.LoadAsync(cancellationToken).ConfigureAwait(true);
        // Case-insensitive, like every other profile lookup keyed on Label rather than an id (ProjectQuickStart,
        // SessionRestorePlanner's caller) — SessionProfile has no separate id (SessionProfile.cs:21).
        var profile = configured.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, pane.ProfileId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new SessionRestorePlan(
                pane,
                null,
                SessionRestoreAvailability.ProfileGone,
                $"The profile '{pane.ProfileId}' no longer exists.")
            { State = state };
        }

        if (state is null)
        {
            return new SessionRestorePlan(
                pane,
                profile,
                SessionRestoreAvailability.Unknown,
                "Nothing is known yet about this session's earlier conversation.");
        }

        if (state.WorktreePath is not null && !Directory.Exists(state.WorktreePath))
        {
            // Host-side and free (no probe): the isolated worktree this session ran in is simply gone from disk.
            // Checked before the conversation state below — a resume into a working directory that no longer
            // exists is not honest to offer even when the provider's own id is still known.
            return new SessionRestorePlan(
                pane,
                profile,
                SessionRestoreAvailability.WorktreeGone,
                "The isolated worktree this session ran in no longer exists.")
            { State = state };
        }

        // AC-539: the same question of the working directory itself — a worktree the cockpit did not mint is recorded
        // there with no WorktreePath, so the check above never sees it, and Claude keys its saved conversations on the
        // directory. Only where a resume would otherwise be offered, so Unsupported keeps stating its own fact.
        if (state.ConversationState == SessionConversationIdState.Known
            && state.WorkingDirectory is { Length: > 0 } workingDirectory
            && !Directory.Exists(workingDirectory))
        {
            return new SessionRestorePlan(
                pane,
                profile,
                SessionRestoreAvailability.WorktreeGone,
                $"The directory this session ran in no longer exists ({workingDirectory}).")
            { State = state };
        }

        return state.ConversationState switch
        {
            SessionConversationIdState.Unsupported => new SessionRestorePlan(
                pane, profile, SessionRestoreAvailability.Unsupported, "This provider does not keep a resumable conversation.") { State = state },
            SessionConversationIdState.Known => new SessionRestorePlan(
                pane, profile, SessionRestoreAvailability.Known, "This session's earlier conversation can be resumed.") { State = state },
            _ => new SessionRestorePlan(
                pane, profile, SessionRestoreAvailability.Unknown, "Nothing is known yet about this session's earlier conversation.") { State = state },
        };
    }
}
