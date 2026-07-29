using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

/// <summary>
/// What a saved AI-session pane can be restored with (AC-410): the profile it resolved to (if any) and how
/// confidently a resume can be offered, plus the reason in words for a banner to show. Composed by
/// <see cref="SessionRestorePlanner"/>; consumed by the restore-offer banner (<c>SessionPanelViewModel.RestoreOffer</c>)
/// and by <c>CockpitViewModel</c> when the operator accepts it.
/// </summary>
/// <param name="Pane">The saved pane this plan is for.</param>
/// <param name="Profile">The profile <see cref="Pane"/>'s <c>ProfileId</c> resolved to, or null when it no longer exists.</param>
/// <param name="Availability">How confidently the earlier conversation can be offered back.</param>
/// <param name="Explanation">Human-readable reason for <see cref="Availability"/>, for a banner to show.</param>
public sealed record SessionRestorePlan(
    WorkspacePane Pane,
    SessionProfile? Profile,
    SessionRestoreAvailability Availability,
    string Explanation)
{
    /// <summary>
    /// The latest <see cref="SessionStateRecord"/> this plan was composed against, or null when none was ever
    /// written (a pane persisted right before a crash, before its session reported anything). Carried so
    /// "Resume conversation" can read the provider's own conversation id straight off the plan instead of
    /// <c>CockpitViewModel</c> re-loading session state it already had.
    /// </summary>
    public SessionStateRecord? State { get; init; }
}
