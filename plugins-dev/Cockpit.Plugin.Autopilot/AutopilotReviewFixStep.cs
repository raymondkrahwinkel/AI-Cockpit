namespace Cockpit.Plugin.Autopilot;

// Builds the one shared fix step a review group's rejected gates are cleared through (AC-434) — read-parallel,
// write-serial: every gate in the group reads the diff concurrently in its own throwaway worktree (see
// `AutopilotRunCoordinator`), and this is the single step that ever writes to the run's real worktree in
// response to what they find. Not CEO-planned — `AutopilotRunDriver` inserts it into the running plan only
// when a gate actually rejects, so a clean pair of gates never pays for a fix pass at all.
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
            // non-adjacent review-gate group (AC-434's contiguity rule keeps them separate groups), and each starts
            // counting its own rounds at 1. Without the lead id, two such groups would both insert "review-fix-1"
            // and InsertStep/_MutateStep would silently conflate them (found in a confirming adversarial pass).
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
