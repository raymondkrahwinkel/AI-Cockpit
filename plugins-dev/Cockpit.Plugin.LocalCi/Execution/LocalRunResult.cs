namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>
/// What one local run produced. Everything that shows a run — the window, the session badge, the MCP tool, the
/// pull-request gate — reads this record rather than the log, so they cannot disagree about what happened.
/// </summary>
/// <param name="LogTail">The last stretch of output, bounded. Empty unless the run actually produced output.</param>
internal sealed record LocalRunResult(
    string WorkflowPath,
    string JobId,
    LocalRunOutcome Outcome,
    TimeSpan Duration,
    int? ExitCode,
    string? Reason,
    string LogTail)
{
    /// <summary>True only when act reached a verdict. The four other endings are silence, not a green light.</summary>
    public bool ReachedAVerdict => Outcome is LocalRunOutcome.Passed or LocalRunOutcome.Failed;

    public static LocalRunResult DidNotRun(string workflowPath, string jobId, LocalRunOutcome outcome, string reason) =>
        new(workflowPath, jobId, outcome, TimeSpan.Zero, ExitCode: null, reason, LogTail: string.Empty);

    /// <summary>
    /// The one line every surface shows. It says "on this machine" and never "CI", because act's own documentation
    /// warns that its images differ from GitHub's runner images — a sentence that reads as "the pull-request check
    /// passed" would be a claim this plugin is not entitled to make.
    /// </summary>
    public string Headline => Outcome switch
    {
        LocalRunOutcome.Passed => $"{JobId} passed on this machine in {_Elapsed}.",
        LocalRunOutcome.Failed => $"{JobId} failed on this machine after {_Elapsed}.",
        LocalRunOutcome.Refused => $"{JobId} was not run on this machine: {Reason}",
        LocalRunOutcome.CouldNotRun => $"{JobId} could not be run on this machine: {Reason}",
        LocalRunOutcome.NotApproved => $"{JobId} was not run on this machine: {Reason}",
        LocalRunOutcome.Cancelled => $"{JobId} was stopped after {_Elapsed} and reached no verdict.",
        _ => $"{JobId} was not run on this machine: {Reason}",
    };

    private string _Elapsed => Duration.TotalMinutes >= 1
        ? $"{(int)Duration.TotalMinutes}m {Duration.Seconds}s"
        : $"{Duration.TotalSeconds:0.#}s";
}
