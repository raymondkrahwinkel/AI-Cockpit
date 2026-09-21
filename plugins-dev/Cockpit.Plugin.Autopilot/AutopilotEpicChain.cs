namespace Cockpit.Plugin.Autopilot;

// AC-1340: after a settled epic sub, the next Ready sub starts itself — strictly one at a time (D4) — or the chain
// stops out loud: one comment on the epic and one note to the cockpit-assistant, never a silent end. The decision
// reads structured facts (the run's record, the gate's landing) and never the text of a comment.
internal sealed class AutopilotEpicChain(
    Func<CancellationToken, Task<AutopilotEpicOutcome>> resolveNext,
    Func<AutopilotRun, Task<string?>> startPlanning,
    Func<string, CancellationToken, Task> commentEpic,
    Func<string, Task<bool>> notifyAssistant,
    int uncleanRunTolerance)
{
    // Starts the next sub when `settled` earned it, and says on the epic what happened either way. Returns the reason
    // the chain stopped, or null when the next sub's planning round was opened. Never throws past a failed comment.
    public async Task<string?> ContinueAsync(
        AutopilotRunRecord settled,
        IReadOnlyList<AutopilotRunRecord> epicRuns,
        AutopilotMergeResult? merge,
        bool strandedCommits,
        CancellationToken cancellationToken = default)
    {
        var stop = StopReason(settled, epicRuns, merge, strandedCommits, uncleanRunTolerance);
        if (stop is not null)
        {
            await _SayAsync($"Autopilot stopped this epic's chain after {settled.Ticket}: {stop}", cancellationToken).ConfigureAwait(false);
            return stop;
        }

        var next = await resolveNext(cancellationToken).ConfigureAwait(false);
        if (next is { Kind: AutopilotEpicOutcomeKind.Ready, Run: { } run })
        {
            // The sub that just landed reads as unmerged again (no commit subject on the collection branch starts
            // with its id): planning it once more would loop the chain on one sub, unattended, for as long as that holds.
            if (string.Equals(run.IssueId, settled.Ticket, StringComparison.OrdinalIgnoreCase))
            {
                stop = $"{run.IssueId} still reads as unmerged on the collection branch after its merge — no commit there starts with its id";
            }
            else if (await startPlanning(run).ConfigureAwait(false) is { } refused)
            {
                stop = $"{refused}, so {run.IssueId} could not be started";
            }
            else
            {
                await _SayAsync($"Autopilot chained to {run.IssueId} ({run.Title}) after {settled.Ticket}: its planning round is open.", cancellationToken).ConfigureAwait(false);
                return null;
            }

            await _SayAsync($"Autopilot stopped this epic's chain after {settled.Ticket}: {stop}", cancellationToken).ConfigureAwait(false);
            return stop;
        }

        switch (next.Kind)
        {
            case AutopilotEpicOutcomeKind.Complete:
                await _SayAsync($"Autopilot finished this epic's chain after {settled.Ticket}: every sub is merged.", cancellationToken).ConfigureAwait(false);
                return "every sub is merged";
            case AutopilotEpicOutcomeKind.Paused:
                stop = next.Reason ?? "the next sub could not be resolved";
                break;
            case AutopilotEpicOutcomeKind.Ready:
            case AutopilotEpicOutcomeKind.NotEpic:
            default:
                stop = "the epic no longer reads as one (no subtasks found)";
                break;
        }

        await _SayAsync($"Autopilot stopped this epic's chain after {settled.Ticket}: {stop}", cancellationToken).ConfigureAwait(false);
        return stop;
    }

    // Why the settled run does not earn the next sub, or null when it does. Every stop reason the ticket names is a
    // branch here, in the order the facts arise in a run: how it ended, its steps, the gate, then the AC-347 streak.
    internal static string? StopReason(
        AutopilotRunRecord settled,
        IReadOnlyList<AutopilotRunRecord> epicRuns,
        AutopilotMergeResult? merge,
        bool strandedCommits,
        int uncleanRunTolerance)
    {
        if (settled.Outcome == AutopilotPlanPhase.Stopped)
        {
            return "the run was stopped by the operator";
        }

        if (settled.Outcome != AutopilotPlanPhase.MergeReady)
        {
            return $"the run blocked — {settled.BlockReason}";
        }

        var failedGates = settled.Steps.Where(step => step.Status == AutopilotStepStatus.Failed).Select(step => step.Title).ToList();
        if (failedGates.Count > 0)
        {
            return $"a gate ran out of attempts ({string.Join(", ", failedGates)})";
        }

        if (strandedCommits)
        {
            return "a step left commits stranded in a worktree of its own, so the run branch does not carry all of its work";
        }

        if (merge is null)
        {
            return "nothing was merged at the merge gate";
        }

        if (!merge.Merged)
        {
            return $"the merge did not land ({merge.Error ?? "no reason given"})";
        }

        if (merge.BuildExitCode != 0)
        {
            return $"the build on the collection branch's new tip exited {merge.BuildExitCode?.ToString() ?? "with no verdict"}";
        }

        // `epicRuns` is newest-first and already holds `settled`, so the leading non-clean stretch counts it too.
        var uncleanInARow = epicRuns.TakeWhile(record => !AutopilotRunReliability.RanClean(record)).Count();
        return uncleanInARow > uncleanRunTolerance
            ? $"{uncleanInARow} run(s) in a row did not run clean, above the tolerance of {uncleanRunTolerance}"
            : null;
    }

    // Said twice, like the merge gate's outcome: on the epic for the trail, and to the assistant so a stopped chain is
    // seen without anyone opening the epic. Both best-effort — a tracker that is down does not undo the decision.
    private async Task _SayAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await commentEpic(text, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The assistant note below still carries it.
        }

        try
        {
            _ = await notifyAssistant(text).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail-soft, as every other host call at a settle is.
        }
    }
}
