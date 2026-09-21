using System.Text;

namespace Cockpit.Plugin.Autopilot;

// The words the merge gate uses (AC-1338): the evidence package the assistant judges, and the epic comment that
// records what happened at the gate. Pure builders, so the wording is tested without a run, git or a tracker.
internal static class AutopilotMergeGateBrief
{
    // What the assistant is handed as its second read (EpicWorkflow §3 step 2): the PR, the three-dot diff-stat
    // against the collection branch, the head sha, every review gate's verdict, the branch's own build, and how to
    // answer — so it can refuse a diff that undoes someone else's work before anything is merged.
    public static string Evidence(AutopilotPlan? plan, string collectionBranch, string? prUrl, AutopilotMergeEvidence evidence, AutopilotMergeMode mode)
    {
        var issue = _Issue(plan);
        var text = new StringBuilder();
        text.Append("Autopilot merge gate — ").Append(issue).Append(" is ready to land on ").Append(collectionBranch).AppendLine(".");
        text.Append("Pull request: ").AppendLine(string.IsNullOrWhiteSpace(prUrl) ? "none opened" : prUrl);
        text.Append("HEAD: ").AppendLine(string.IsNullOrWhiteSpace(evidence.HeadSha) ? "unknown" : evidence.HeadSha);
        text.AppendLine("Review gates:");
        foreach (var gate in plan?.Steps.Where(step => step.IsReviewGate) ?? [])
        {
            text.Append("- ").Append(gate.Title).Append(": ").Append(gate.Status);
            if (!string.IsNullOrWhiteSpace(gate.Note))
            {
                text.Append(" — ").Append(gate.Note.Trim());
            }

            text.AppendLine();
        }

        text.Append("Build: ").AppendLine(_Build(evidence.BuildExitCode));
        text.Append("git diff --stat origin/").Append(collectionBranch).AppendLine("...HEAD:");
        text.AppendLine(string.IsNullOrWhiteSpace(evidence.DiffStat) ? "(empty — nothing differs from the collection branch)" : evidence.DiffStat);
        if (!string.IsNullOrWhiteSpace(evidence.Error))
        {
            text.Append("Measurement error: ").AppendLine(evidence.Error);
        }

        // The build tail comes last: the host bounds the message, and a cut-off must land here, not on the diff-stat.
        if (!evidence.BuildPassed && !string.IsNullOrWhiteSpace(evidence.BuildOutputTail))
        {
            text.Append("Build output (tail):").AppendLine().AppendLine(evidence.BuildOutputTail);
        }

        text.Append(mode == AutopilotMergeMode.Automatic
            ? "Mode: automatic — Autopilot merges now without waiting; this is for your record."
            : $"Mode: explicit — nothing is merged until a go. Read the diff-stat for deletions of files another sub added, then answer with the {AutopilotMergeGateTools.EndpointName} tool autopilot_merge_go(issue: \"{issue}\", go: true|false, reason). The operator can also answer on the run itself.");
        return text.ToString();
    }

    // The line on the run's surface while the gate waits, so the operator sees what the run is waiting for and on whom.
    public static string Waiting(string collectionBranch) =>
        $"Both review gates passed — waiting at the merge gate for a go (operator or cockpit-assistant) before landing on {collectionBranch}.";

    // The epic comment for a gate that refused before anything moved: a red build of the branch, or a no from whoever read it.
    public static string Refused(AutopilotPlan? plan, string collectionBranch, string by, string? reason) =>
        $"Autopilot merge gate: {_Issue(plan)} was NOT merged into {collectionBranch} — refused by {by}{_Reason(reason)}. The branch and its pull request are left as they are.";

    // The epic comment for a landed sub (EpicWorkflow §3 steps 4-6): how it landed, the new tip, and the build's exit code on it.
    public static string Merged(AutopilotPlan? plan, string collectionBranch, string by, AutopilotMergeResult result, string buildCommand)
    {
        var build = result.BuildExitCode is { } exit
            ? $"`{buildCommand}` on that tip exited {exit}{(exit == 0 ? string.Empty : " — the collection branch is red; the next sub is held until it is fixed")}."
            : $"the build did not run{_Reason(result.Error)}.";
        return $"Autopilot merge gate: {_Issue(plan)} merged into {collectionBranch} on a go from {by} — {result.Route}; tip {result.TipSha ?? "unknown"}. Build: {build}";
    }

    // The epic comment for a go that could not be carried out: the rebase, push or merge itself refused.
    public static string Failed(AutopilotPlan? plan, string collectionBranch, string? error) =>
        $"Autopilot merge gate: the go for {_Issue(plan)} could not be carried out on {collectionBranch}{_Reason(error)}.";

    private static string _Issue(AutopilotPlan? plan) => plan?.Source?.IssueId is { Length: > 0 } issue ? issue : plan?.Label ?? "the run";

    private static string _Build(int? exitCode) => exitCode switch
    {
        null => "not run (no build command configured)",
        0 => "passed (exit 0)",
        var code => $"FAILED (exit {code})",
    };

    private static string _Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}";
}
