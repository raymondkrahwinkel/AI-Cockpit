namespace Cockpit.Plugin.Autopilot;

// Builds the one shared fix step a review group's rejected gates are cleared through (AC-434) — read-parallel,
// write-serial: gates read the diff concurrently in their own throwaway worktrees, and this is the single step
// that writes to the run's real worktree. Not CEO-planned — `AutopilotRunDriver` inserts it only on a rejection.
internal static class AutopilotReviewFixStep
{
    public static AutopilotStep Build(AutopilotStep lead, IReadOnlyList<AutopilotStep> openGates, int round)
    {
        var findings = string.Join(
            "\n\n",
            openGates.Select(gate =>
                $"- {gate.Title}: {(string.IsNullOrWhiteSpace(gate.Note) ? "(no reason recorded)" : gate.Note.Trim())}"));

        return new AutopilotStep(
            // Scoped by the group's own lead step id, not just the round — a plan can carry more than one
            // non-adjacent review-gate group, each counting its own rounds at 1. Without the lead id, two such
            // groups would both insert "review-fix-1" and silently conflate (found in an adversarial pass).
            $"review-fix-{lead.Id}-{round}",
            "Apply review findings",
            string.Empty,
            lead.ProfileLabel,
            lead.Model,
            $"""
            The review gate(s) below rejected the current diff. Resolve every finding — do not touch anything outside
            what they raise:

            {findings}
            """.ReplaceLineEndings("\n"), // AC-1051: raw string literals take the source file's line endings.
            "Every finding above is resolved in the diff, and the project builds and its test suite passes (run it once here) with no new warnings.",
            GateMode.Skip);
    }
}
