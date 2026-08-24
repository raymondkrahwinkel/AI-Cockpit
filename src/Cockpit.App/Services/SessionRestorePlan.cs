using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Workspaces;

namespace Cockpit.App.Services;

// AC-1013 (was AC-410): what a saved AI-session pane can be restored with — resolved profile, resume
// confidence, and banner explanation — composed by SessionRestorePlanner and consumed by the restore-offer
// banner and CockpitViewModel.
public sealed record SessionRestorePlan(
    WorkspacePane Pane,
    SessionProfile? Profile,
    SessionRestoreAvailability Availability,
    string Explanation)
{
    // AC-1013: latest SessionStateRecord for this plan, or null if none was ever written; lets "Resume
    // conversation" read the conversation id from the plan instead of re-loading session state.
    public SessionStateRecord? State { get; init; }
}
