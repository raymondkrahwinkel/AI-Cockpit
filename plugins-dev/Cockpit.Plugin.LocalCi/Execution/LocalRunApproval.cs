using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Execution;

// <summary>Whether a job may be attempted here, and — when it may — the runner label its image is chosen for.</summary>
// `Reason`: Completes "this job was not run because …". Empty when it was approved.
// `SetupActions`:
// The `uses:` references this job declares (AC-617), so a failure in one of them can be told apart from a
// failure in the project's own `run:` steps — see `SetupFailure`. Empty for a refusal, and for a
// job that uses no actions at all.
//
internal sealed record JobApproval(bool IsApproved, string? RunnerLabel, string Reason, IReadOnlyList<string> SetupActions)
{
    public static JobApproval No(string reason) => new(IsApproved: false, null, reason, []);

    public static JobApproval Yes(string runnerLabel, IReadOnlyList<string> setupActions) =>
        new(IsApproved: true, runnerLabel, string.Empty, setupActions);
}

// Asks the classification whether a job may run, and nothing more.
//
// This is the rule the epic calls non-negotiable: a job runs whole or not at all. The executing side gets no
// opinion of its own — it re-reads the project's workflows and takes the verdict the classifier gives, reason
// included. A second, more permissive judgement here is exactly how a run that skipped steps ends up green.
internal static class LocalRunApproval
{
    public static JobApproval For(LocalRunRequest request)
    {
        var workflowName = Path.GetFileName(request.WorkflowPath);

        var read = WorkflowCatalog.ReadProject(request.ProjectRoot)
            .FirstOrDefault(result => string.Equals(result.Path, request.WorkflowPath, StringComparison.OrdinalIgnoreCase));

        if (read is null)
        {
            return JobApproval.No($"{workflowName} is not one of this project's workflows.");
        }

        if (read.Document is not { } document)
        {
            return JobApproval.No(read.Error ?? $"{workflowName} could not be read.");
        }

        if (document.Jobs.FirstOrDefault(job => job.Id == request.JobId) is not { } job)
        {
            return JobApproval.No($"{workflowName} has no job called {request.JobId}.");
        }

        if (LocalRunClassifier.Classify(document).FirstOrDefault(verdict => verdict.JobId == request.JobId) is not { } verdict)
        {
            return JobApproval.No($"{workflowName} has no job called {request.JobId}.");
        }

        if (!verdict.CanRunLocally)
        {
            return JobApproval.No(verdict.Reason ?? "it cannot run on this machine.");
        }

        // The image is chosen per runner label, so a job with nothing single to name cannot be started even if it
        // were otherwise fine. Today the classifier already refuses those; this stays because the alternative is to
        // read the label as if it must be there, and a classification that later grows more permissive would then
        // fail here as a crash rather than as an answer.
        return job.RunsOn.Label is { } runnerLabel
            ? JobApproval.Yes(runnerLabel, [.. job.Steps.Select(step => step.Uses).OfType<string>()])
            : JobApproval.No($"{request.JobId} does not name a single runner to pick an image for.");
    }
}
