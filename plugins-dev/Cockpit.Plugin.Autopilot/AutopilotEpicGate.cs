namespace Cockpit.Plugin.Autopilot;

// The mid- and end-of-epic gate (AC-1341) — EpicWorkflow §5, §6d and §14 in automated form: main brought in, the
// full suite on a pinned tip with TRX left behind, the red set read against a recorded baseline, every merged sub's
// test files checked on the tip, its test count read against its budget.
//
// It repairs nothing: the report goes on the epic and to the assistant, and at the end of the epic the pull request
// to main waits for an explicit go.
internal sealed class AutopilotEpicGate(
    IAutopilotEpicGateExecutor executor,
    string epicId,
    string collectionBranch,
    string worktreePath,
    string suiteCommand,
    TimeSpan suiteTimeout,
    string? baselineDirectory,
    Func<string, CancellationToken, Task<string?>> describeSub,
    Func<string, CancellationToken, Task<AutopilotMergeGo>> awaitGo,
    Func<string, CancellationToken, Task> commentEpic,
    Func<string, Task<bool>> notifyAssistant)
{
    public async Task<AutopilotEpicGateVerdict> RunAsync(AutopilotEpicGateKind kind, IReadOnlyList<string> mergedSubs, CancellationToken cancellationToken = default)
    {
        var measurement = await executor.MeasureAsync(worktreePath, collectionBranch, mergedSubs, suiteCommand, suiteTimeout, cancellationToken).ConfigureAwait(false);
        var tip = AutopilotTrxResults.Load(measurement.ResultsDirectory);
        var baseline = AutopilotTrxResults.Load(baselineDirectory);

        var budgets = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var sub in mergedSubs)
        {
            budgets[sub] = await _BudgetAsync(sub, cancellationToken).ConfigureAwait(false);
        }

        var verdict = AutopilotEpicGateBrief.Report(kind, epicId, collectionBranch, measurement, suiteCommand, tip, baseline, baselineDirectory, budgets);
        if (kind == AutopilotEpicGateKind.Mid)
        {
            await _SayAsync(verdict.Report, cancellationToken).ConfigureAwait(false);
            return verdict;
        }

        // §14 step 7: the report is what the go is asked against — findings included, since the reader decides.
        await _SayAsync(verdict.Report + Environment.NewLine + AutopilotEpicGateBrief.HowToAnswer(epicId), cancellationToken).ConfigureAwait(false);
        var go = await awaitGo(epicId, cancellationToken).ConfigureAwait(false);
        if (!go.Go)
        {
            await _SayAsync(AutopilotEpicGateBrief.PullRequestRefused(epicId, collectionBranch, go.By, go.Reason), cancellationToken).ConfigureAwait(false);
            return verdict;
        }

        var pullRequest = await executor.OpenPullRequestAsync(worktreePath, collectionBranch, $"{epicId} — {collectionBranch} to main", verdict.Report, cancellationToken).ConfigureAwait(false);
        var outcome = pullRequest.PrUrl is { Length: > 0 } url
            ? AutopilotEpicGateBrief.PullRequestOpened(epicId, collectionBranch, url, go.By)
            : AutopilotEpicGateBrief.PullRequestFailed(epicId, collectionBranch, pullRequest.Error);
        await _SayAsync(outcome, cancellationToken).ConfigureAwait(false);
        return verdict;
    }

    // A tracker that cannot describe the sub leaves its budget unstated rather than ending the gate.
    private async Task<int?> _BudgetAsync(string sub, CancellationToken cancellationToken)
    {
        try
        {
            return AutopilotEpicGateBrief.Budget(await describeSub(sub, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private Task _SayAsync(string text, CancellationToken cancellationToken) =>
        AutopilotEpicChain.SayAsync(commentEpic, notifyAssistant, text, cancellationToken);
}
