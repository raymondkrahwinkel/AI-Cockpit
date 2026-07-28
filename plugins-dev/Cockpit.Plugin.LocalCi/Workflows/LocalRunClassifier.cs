namespace Cockpit.Plugin.LocalCi.Workflows;

/// <summary>
/// Decides, per job, whether it is worth running on this machine — and when it is not, says which construct decided
/// that. Deciding what we are willing to run is policy and stays ours; act would happily attempt whatever it is
/// handed, and a job that runs half of itself and goes green is worse than a job that never ran.
/// </summary>
/// <remarks>
/// The rule that shapes everything here: <b>anything not understood is refused</b>. Each construct must be on a list
/// to pass, so a workflow key added by GitHub next year makes a job unrunnable rather than quietly ignored. The
/// checks run in a fixed order so a job with two problems always reports the same one, which is what lets a test
/// assert a reason rather than a set of them.
/// </remarks>
internal static class LocalRunClassifier
{
    /// <summary>
    /// The two actions that cost nothing locally: the worktree already is the checkout, and the SDK is in the image.
    /// Every other <c>uses:</c> blocks the job unless someone adds it here with the reason it is free.
    /// </summary>
    private static readonly HashSet<string> LocallyFreeActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions/checkout",
        "actions/setup-dotnet",
    };

    /// <summary>Named apart from the rest only so the reason names the thing the operator recognises.</summary>
    private static readonly HashSet<string> ArtifactActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "actions/upload-artifact",
        "actions/download-artifact",
    };

    /// <summary>
    /// Job keys we have read and that do not change whether the job can run here. <c>permissions</c>, <c>concurrency</c>
    /// and <c>timeout-minutes</c> are on the list because they only govern GitHub's own scheduling; <c>needs</c> is on
    /// it because ordering is not the same as exchanging artifacts, which is caught on its own below.
    /// </summary>
    private static readonly HashSet<string> UnderstoodJobKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "runs-on", "steps", "needs", "if", "env", "defaults", "outputs", "permissions", "concurrency",
        "timeout-minutes", "strategy",
    };

    /// <summary>
    /// What may sit under <c>strategy</c>. <c>matrix</c> is caught first and on its own; the other two only govern
    /// how GitHub schedules a set of runs, which is nothing to a single local one — so a strategy without a matrix
    /// must not be refused merely for existing.
    /// </summary>
    private static readonly HashSet<string> UnderstoodStrategyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "matrix", "fail-fast", "max-parallel",
    };

    private static readonly HashSet<string> UnderstoodStepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "id", "if", "run", "uses", "with", "env", "shell", "working-directory", "timeout-minutes",
    };

    /// <summary>Keys we do understand and refuse anyway — worth their own sentence instead of "not understood".</summary>
    private static readonly Dictionary<string, string> RefusedJobKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["container"] = "it runs the whole job inside a container of its own, which this plugin does not set up",
        ["services"] = "it needs service containers running alongside it",
        ["environment"] = "it targets a deployment environment, which only exists on GitHub",
        // A job-level uses: is a call to another workflow rather than a job with steps of its own. Understood, and
        // refused — but named for what it is, not filed under "not understood".
        ["uses"] = "it calls another workflow instead of running steps of its own",
        ["with"] = "it passes inputs to another workflow instead of running steps of its own",
        ["secrets"] = "it passes secrets to another workflow instead of running steps of its own",
        ["continue-on-error"] = "it uses continue-on-error, which decides whether a failure counts — and act ignores it, "
            + "so a local result would not mean the same thing",
    };

    private static readonly Dictionary<string, string> RefusedStepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["continue-on-error"] = "a step uses continue-on-error, which decides whether a failure counts — and act ignores "
            + "it, so a local result would not mean the same thing",
    };

    public static IReadOnlyList<JobVerdict> Classify(WorkflowDocument document) =>
        document.Jobs.Select(job => _ClassifyJob(document, job)).ToList();

    private static JobVerdict _ClassifyJob(WorkflowDocument document, WorkflowJob job)
    {
        if (job.HasMatrix)
        {
            return JobVerdict.Cannot(document, job, "it uses a matrix, so it is several runs rather than one");
        }

        if (job.StrategyKeys.FirstOrDefault(key => !UnderstoodStrategyKeys.Contains(key)) is { } unknownStrategyKey)
        {
            return JobVerdict.Cannot(document, job, $"its strategy uses \"{unknownStrategyKey}\", which this check does not understand");
        }

        if (_FirstRefused(job.Keys, RefusedJobKeys) is { } refusedJobKey)
        {
            return JobVerdict.Cannot(document, job, refusedJobKey);
        }

        if (job.Keys.FirstOrDefault(key => !UnderstoodJobKeys.Contains(key)) is { } unknownJobKey)
        {
            return JobVerdict.Cannot(document, job, $"it uses \"{unknownJobKey}\", which this check does not understand");
        }

        if (_RunsOnReason(job.RunsOn) is { } runsOnReason)
        {
            return JobVerdict.Cannot(document, job, runsOnReason);
        }

        if (job.Steps.Count == 0)
        {
            // "Nothing to do" is not the same as "runs fine". A job with no steps we can see is one we have read
            // wrongly, and reporting it as runnable would put a green tick on a job that does nothing.
            return JobVerdict.Cannot(document, job, "it has no steps");
        }

        if (job.Steps.Select(_StepUsesReason).FirstOrDefault(reason => reason is not null) is { } usesReason)
        {
            return JobVerdict.Cannot(document, job, usesReason);
        }

        if (job.Steps.Select(_StepKeyReason).FirstOrDefault(reason => reason is not null) is { } stepKeyReason)
        {
            return JobVerdict.Cannot(document, job, stepKeyReason);
        }

        return JobVerdict.CanRun(document, job);
    }

    private static string? _RunsOnReason(RunsOnSpec runsOn) => runsOn switch
    {
        { Kind: RunsOnKind.Missing } => "it does not say what it runs on",
        { Kind: RunsOnKind.Expression } => "its runs-on is an expression, and what that resolves to is only known on GitHub",
        { Kind: RunsOnKind.NotUnderstood } => "its runs-on is written in a form this check does not understand",
        { Label: { } label } when !_IsLinuxLabel(label) => $"it needs a {label} runner, and only Linux runners can run here",
        _ => null,
    };

    private static string? _StepUsesReason(WorkflowStep step)
    {
        if (step.Uses is null)
        {
            return null;
        }

        if (step.ActionId is not { Length: > 0 } action)
        {
            // A uses: that is present but empty is not a run: step — waving it through would be the one thing this
            // classification promises not to do.
            return "a step has an empty uses:, and an empty action is not something to assume about";
        }

        // Three shapes that all refuse, but for three different reasons — and only one of them is "GitHub". Saying
        // that about a composite action in this very repository, or about a container action, would be a lie, and a
        // reason the operator cannot act on is no better than no reason.
        if (action.StartsWith("./", StringComparison.Ordinal) || action.StartsWith("../", StringComparison.Ordinal))
        {
            return $"it uses {action}, an action from this repository, which this check does not run";
        }

        if (action.StartsWith("docker://", StringComparison.OrdinalIgnoreCase))
        {
            return $"it uses {action}, a container action, which this check does not run";
        }

        if (ArtifactActions.Contains(action))
        {
            return $"it exchanges artifacts with another job (it uses {action})";
        }

        return LocallyFreeActions.Contains(action)
            ? null
            : $"it uses {action}, which only means something on GitHub";
    }

    private static string? _StepKeyReason(WorkflowStep step) =>
        _FirstRefused(step.Keys, RefusedStepKeys)
        ?? (step.Keys.FirstOrDefault(key => !UnderstoodStepKeys.Contains(key)) is { } unknown
            ? $"a step uses \"{unknown}\", which this check does not understand"
            : null);

    private static string? _FirstRefused(IReadOnlyList<string> keys, Dictionary<string, string> refused) =>
        keys.Select(key => refused.GetValueOrDefault(key)).FirstOrDefault(reason => reason is not null);

    /// <summary>act runs Linux images; anything else — windows, macos, a self-hosted label — is not ours to assume.</summary>
    private static bool _IsLinuxLabel(string label) => label.StartsWith("ubuntu-", StringComparison.OrdinalIgnoreCase);
}
