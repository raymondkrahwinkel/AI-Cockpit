namespace Cockpit.Plugin.Autopilot;

// What the merge-ready finalization actually landed (AC-216), handed on to the merge gate (AC-1338): whether the
// run branch reached the remote — a gate cannot merge a branch nobody can fetch — and the PR it opened, if any.
internal sealed record AutopilotPrPublishOutcome(bool Pushed, string? PrUrl);
