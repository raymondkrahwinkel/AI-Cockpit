using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// What a saved AI-session pane can be restored with (AC-410): the profile it resolved to (if any) and how
// confidently a resume can be offered, plus the reason in words for a banner to show. Composed by
// `SessionRestorePlanner`; consumed by the restore-offer banner (`SessionPanelViewModel.RestoreOffer`)
// and by `CockpitViewModel` when the operator accepts it.
//
// `Pane`: The saved pane this plan is for.
// `Profile`: The profile `Pane`'s `ProfileId` resolved to, or null when it no longer exists.
// `Availability`: How confidently the earlier conversation can be offered back.
// `Explanation`: Human-readable reason for `Availability`, for a banner to show.
public sealed record SessionRestorePlan(
    WorkspacePane Pane,
    SessionProfile? Profile,
    SessionRestoreAvailability Availability,
    string Explanation)
{
    // The latest `SessionStateRecord` this plan was composed against, or null when none was ever
    // written (a pane persisted right before a crash, before its session reported anything). Carried so
    // "Resume conversation" can read the provider's own conversation id straight off the plan instead of
    // `CockpitViewModel` re-loading session state it already had.
    public SessionStateRecord? State { get; init; }
}
