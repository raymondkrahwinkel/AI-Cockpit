namespace Cockpit.Plugin.LocalCi.Workflows;

/// <summary>
/// What we are willing to say about one job: it can run on this machine, or it cannot and here is why. The reason
/// completes the sentence "this job cannot run locally because …", so it reads as an explanation rather than a code.
/// </summary>
internal sealed record JobVerdict(string WorkflowPath, string JobId, string? JobName, bool CanRunLocally, string? Reason)
{
    public static JobVerdict CanRun(WorkflowDocument document, WorkflowJob job) =>
        new(document.Path, job.Id, job.Name, CanRunLocally: true, null);

    public static JobVerdict Cannot(WorkflowDocument document, WorkflowJob job, string reason) =>
        new(document.Path, job.Id, job.Name, CanRunLocally: false, reason);

    /// <summary>What to put on screen: the job's own name when it has one, otherwise its key in the file.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(JobName) ? JobId : JobName;
}
