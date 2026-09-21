namespace Cockpit.Plugin.Autopilot;

// An answer at the merge gate (AC-1338): go or no, why, and who said so — the operator's button, the go tool's
// verified caller, or Autopilot itself in automatic mode. `By` lands in the epic comment.
internal sealed record AutopilotMergeGo(bool Go, string? Reason, string By);
