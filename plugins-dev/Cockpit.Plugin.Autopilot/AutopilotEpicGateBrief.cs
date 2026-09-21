using System.Text;
using System.Text.RegularExpressions;

namespace Cockpit.Plugin.Autopilot;

// The words the epic gate uses (AC-1341): the report the epic and the assistant get, the budget read off a ticket,
// and the comments around the pull request to main. Pure builders, so the judgement is tested without git, dotnet
// or a tracker — and, per EpicWorkflow §4, so each line can only be satisfied by the thing it reports on.
internal static partial class AutopilotEpicGateBrief
{
    // Up to a few non-word characters between the label and the number: a colon, markdown bold, a space.
    [GeneratedRegex(@"\btest\s*budget\W{0,6}(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BudgetPattern();

    // The test budget a groomed ticket states ("Testbudget: 5", "**Test budget:** 5"), or null when it states none.
    public static int? Budget(string? description) =>
        description is not null && BudgetPattern().Match(description) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var budget) ? budget : null;

    public static AutopilotEpicGateVerdict Report(
        AutopilotEpicGateKind kind,
        string epicId,
        string collectionBranch,
        AutopilotEpicGateMeasurement measurement,
        string suiteCommand,
        AutopilotTrxResults? tip,
        AutopilotTrxResults? baseline,
        string? baselineDirectory,
        IReadOnlyDictionary<string, int?> budgets)
    {
        var findings = new List<string>();
        var text = new StringBuilder();
        var when = kind == AutopilotEpicGateKind.End ? "end of epic" : $"mid-epic, after {measurement.Subs.Count} merged sub(s)";
        text.Append("Autopilot epic gate (").Append(when).Append(") — ").Append(epicId).Append(" on ").Append(collectionBranch).Append(" at ").Append(string.IsNullOrWhiteSpace(measurement.TipSha) ? "an unknown tip" : measurement.TipSha).AppendLine(".");
        text.Append("Main brought in: ").AppendLine(measurement.MainBroughtIn);
        if (measurement.Error is { Length: > 0 } error)
        {
            findings.Add($"the gate could not measure: {error}");
        }

        _Suite(text, findings, measurement, suiteCommand, tip);
        _RedSet(text, findings, tip, _ValidBaseline(text, findings, measurement.MainSha, baseline, baselineDirectory));
        _TestFiles(text, findings, measurement.Subs);
        _TestCounts(text, measurement.Subs, budgets);

        if (findings.Count == 0)
        {
            text.AppendLine("Findings: none.");
        }
        else
        {
            text.Append("Findings (").Append(findings.Count).AppendLine("):");
            foreach (var finding in findings)
            {
                text.Append("- ").AppendLine(finding);
            }
        }

        // The suite's tail comes last: the host bounds a message, so a cut-off must land here and not on a finding.
        if (measurement.SuiteExitCode != 0 && !string.IsNullOrWhiteSpace(measurement.SuiteOutputTail))
        {
            text.AppendLine("Suite output (tail):").Append(measurement.SuiteOutputTail);
        }

        return new AutopilotEpicGateVerdict(text.ToString().TrimEnd(), findings);
    }

    // How the end gate's go is given (§14 step 7) — the same tool and button as the merge gate, addressed to the epic.
    public static string HowToAnswer(string epicId) =>
        $"Nothing is opened until a go: answer with the {AutopilotMergeGateTools.EndpointName} tool autopilot_merge_go(issue: \"{epicId}\", go: true|false, reason) to open the pull request from the collection branch to main, or refuse it. The operator can also answer on the Autopilot surface.";

    public static string PullRequestOpened(string epicId, string collectionBranch, string url, string by) =>
        $"Autopilot epic gate: the pull request from {collectionBranch} to main for {epicId} is open on a go from {by}: {url}. The merge itself stays with the operator.";

    public static string PullRequestRefused(string epicId, string collectionBranch, string by, string? reason) =>
        $"Autopilot epic gate: no pull request from {collectionBranch} to main for {epicId} — refused by {by}{_Reason(reason)}. The branch is left as it is.";

    public static string PullRequestFailed(string epicId, string collectionBranch, string? error) =>
        $"Autopilot epic gate: the go for {epicId} was given but the pull request from {collectionBranch} to main could not be opened{_Reason(error)}. Open it by hand.";

    private static void _Suite(StringBuilder text, List<string> findings, AutopilotEpicGateMeasurement measurement, string suiteCommand, AutopilotTrxResults? tip)
    {
        var exit = measurement.SuiteExitCode is { } code ? $"exited {code}" : "gave no verdict";
        text.Append("Suite: `").Append(suiteCommand).Append("` ").Append(exit);
        if (tip is null)
        {
            text.Append(" — no TRX report in ").Append(measurement.ResultsDirectory).AppendLine(".");
            findings.Add($"the suite left no TRX report in {measurement.ResultsDirectory}");
            return;
        }

        text.Append(" — ").Append(tip.Total).Append(" tests, ").Append(tip.Passed).Append(" passed, ").Append(tip.FailedTests.Count).Append(" failed (").Append(tip.Files).Append(" TRX in ").Append(measurement.ResultsDirectory).AppendLine(").");
        if (measurement.SuiteExitCode is null)
        {
            findings.Add("the suite gave no verdict (it did not run to its end)");
        }
    }

    // EpicWorkflow §6d: the baseline is a recorded suite run on origin/main with its sha beside the TRX. Missing or
    // sha-less, the gate has no verdict on the red set and says so (§6b). Recorded on an older main it still
    // classifies — environment failures are sha-independent — under one named caveat; the reader decides.
    private static AutopilotTrxResults? _ValidBaseline(StringBuilder text, List<string> findings, string mainSha, AutopilotTrxResults? baseline, string? baselineDirectory)
    {
        var gap = baseline switch
        {
            _ when string.IsNullOrWhiteSpace(baselineDirectory) => "no baseline directory is set",
            null => $"no TRX report in {baselineDirectory}",
            { Sha: null } => $"the baseline in {baselineDirectory} has no recorded sha ({AutopilotTrxResults.ManifestFileName})",
            _ => null,
        };

        if (gap is not null || baseline?.Sha is not { } sha)
        {
            text.Append("Baseline: no verdict — ").Append(gap).AppendLine(".");
            findings.Add($"no verdict on the red set: {gap}");
            return null;
        }

        text.Append("Baseline: ").Append(baselineDirectory).Append(" recorded at ").Append(_Short(sha));
        text.AppendLine(_SameCommit(sha, mainSha)
            ? $" (origin/main), {baseline.Total} tests, {baseline.FailedTests.Count} red."
            : $", origin/main is now {_Short(mainSha)} — environment classification is against an older main; a test fixed on main since then and red on the tip reads as environment here. {baseline.Total} tests, {baseline.FailedTests.Count} red.");
        return baseline;
    }

    private static bool _SameCommit(string recorded, string current) =>
        recorded.Length > 0 && current.Length > 0
        && (recorded.StartsWith(current, StringComparison.OrdinalIgnoreCase) || current.StartsWith(recorded, StringComparison.OrdinalIgnoreCase));

    private static string _Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    // Red on the tip and red on the baseline is the environment; red on the tip and not on the baseline is ours.
    // Without a usable baseline the red tests are listed unclassified — the finding above already holds the chain.
    private static void _RedSet(StringBuilder text, List<string> findings, AutopilotTrxResults? tip, AutopilotTrxResults? baseline)
    {
        if (tip is null || tip.FailedTests.Count == 0)
        {
            return;
        }

        var red = tip.FailedTests.Order(StringComparer.Ordinal).ToList();
        if (baseline is null)
        {
            text.Append("Red on the tip (").Append(red.Count).Append("), unclassified: ").AppendLine(string.Join(", ", red));
            return;
        }

        var environment = red.Where(baseline.FailedTests.Contains).ToList();
        var ours = red.Where(test => !baseline.FailedTests.Contains(test)).ToList();
        text.Append("Red on the tip (").Append(red.Count).Append("), against the baseline's ").Append(baseline.FailedTests.Count).AppendLine(" red:");
        text.Append("- environment (also red on the baseline): ").AppendLine(environment.Count == 0 ? "none" : string.Join(", ", environment));
        text.Append("- ours (not red on the baseline): ").AppendLine(ours.Count == 0 ? "none" : string.Join(", ", ours));
        if (ours.Count > 0)
        {
            findings.Add($"{ours.Count} test(s) red on the tip and not on the baseline — ours: {string.Join(", ", ours)}");
        }
    }

    // §5: a test that a rebase dropped is invisible in a green suite — the suite simply no longer runs it.
    private static void _TestFiles(StringBuilder text, List<string> findings, IReadOnlyList<AutopilotEpicGateSubMeasurement> subs)
    {
        text.AppendLine("Test files per merged sub:");
        foreach (var sub in subs)
        {
            text.Append("- ").Append(sub.IssueId).Append(": ");
            if (sub.AddedTestFiles.Count == 0)
            {
                text.AppendLine("none added");
                continue;
            }

            text.Append(sub.AddedTestFiles.Count).Append(" added, ");
            if (sub.MissingTestFiles.Count == 0)
            {
                text.AppendLine("all still on the tip");
                continue;
            }

            text.Append("MISSING on the tip: ").AppendLine(string.Join(", ", sub.MissingTestFiles));
            findings.Add($"{sub.IssueId}'s test file(s) are gone from the tip: {string.Join(", ", sub.MissingTestFiles)}");
        }
    }

    // Reported, not a finding: the budget is the grooming's steer, and whether an overrun matters is the reader's call.
    private static void _TestCounts(StringBuilder text, IReadOnlyList<AutopilotEpicGateSubMeasurement> subs, IReadOnlyDictionary<string, int?> budgets)
    {
        text.AppendLine("Test count per merged sub (Fact/Theory/InlineData attributes added):");
        foreach (var sub in subs)
        {
            text.Append("- ").Append(sub.IssueId).Append(": ").Append(sub.TestAttributesAdded);
            var budget = budgets.GetValueOrDefault(sub.IssueId);
            text.AppendLine(budget is { } stated
                ? sub.TestAttributesAdded > stated ? $" — OVER the budget of {stated}" : $", budget {stated}"
                : ", no budget stated on the ticket");
        }
    }

    private static string _Reason(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason.Trim()}";
}
