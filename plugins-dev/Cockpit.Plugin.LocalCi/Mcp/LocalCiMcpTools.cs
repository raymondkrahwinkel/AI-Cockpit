using System.ComponentModel;
using System.Text.Json;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugin.LocalCi.Workflows;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.LocalCi.Mcp;

// The `cockpit-local-ci` tool surface: how a session checks its own work before pushing it.
//
// Neither tool takes a project or a path. Which checkout is acted on comes from
// `ICockpitHost.CurrentMcpCallerPaneId` — the pane the transport says made the call — so a session
// cannot ask for a run in somebody else's tree, and a prompt-injected one cannot be talked into it. That is the
// whole reason the signatures look narrower than they could be.
internal sealed class LocalCiMcpTools(
    ICockpitHost host,
    SessionCheckouts checkouts,
    ILocalJobRunner runner,
    LocalRunTracker tracker,
    GitHead head,
    LocalCiSettings settings)
{
    [McpServerTool(Name = "run_local_checks")]
    [Description(
        "Runs one of this project's GitHub workflow jobs in a container on this machine, using this session's own "
        + "checkout, and returns the verdict. The project is the calling session's — this tool takes no path and "
        + "will not run anywhere else. A job that cannot run locally (a matrix, a non-Linux runner, artifacts "
        + "exchanged with another job) is refused with the reason rather than partly run. The operator is asked to "
        + "approve the exact command before anything starts. The verdict is about this machine: act's images are "
        + "not GitHub's, so it predicts the pull-request check and does not replace it.")]
    public async Task<string> RunLocalChecks(
        [Description("The job to run, named as it is in the workflow (e.g. \"build\"). Leave it out to run the first job in this project that can run here.")]
        string? job = null,
        CancellationToken cancellationToken = default)
    {
        if (_CheckoutOfCaller() is not { } caller)
        {
            return _CannotIdentifyCaller();
        }

        var (paneId, checkout) = caller;

        if (_Choose(checkout, job) is not { } chosen)
        {
            return McpJson.Error(job is { Length: > 0 }
                ? $"No job called {job} in this project can run on this machine. Ask for local_check_status to see what can."
                : "No job in this project's workflows can run on this machine.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var commit = await head.ReadAsync(checkout, cancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        tracker.Begin(checkout, chosen.JobId, startedAt, () =>
        {
            stopping.Cancel();
            return Task.CompletedTask;
        });

        try
        {
            var result = await runner.RunAsync(
                chosen,
                _ => { },
                command => _AskOperatorAsync(command, checkout, paneId),
                stopping.Token);

            tracker.Complete(checkout, result, commit, DateTimeOffset.UtcNow);
            return McpJson.Of(_Report(result));
        }
        catch (Exception exception)
        {
            // Whatever went wrong, the run is over. Without this the status bar would keep offering a Kill for
            // something that is not running, which is worse than showing nothing. A stop and a fault are recorded
            // as the different things they are: filing a bug in the runner as "cancelled" would tell the operator
            // that somebody stopped this, and nobody did.
            var (outcome, reason) = exception is OperationCanceledException
                ? (LocalRunOutcome.Cancelled, "the run was stopped before it reached a verdict.")
                : (LocalRunOutcome.CouldNotRun, $"the run ended in an error: {exception.Message}");

            tracker.Complete(
                checkout,
                LocalRunResult.DidNotRun(chosen.WorkflowPath, chosen.JobId, outcome, reason),
                commit,
                DateTimeOffset.UtcNow);
            throw;
        }
    }

    [McpServerTool(Name = "local_check_status")]
    [Description(
        "Reports what this project's workflow jobs are — which of them can run on this machine and, for each that "
        + "cannot, why — plus the last local run in this session's checkout and whether it was on the commit that "
        + "is checked out now. The project is the calling session's.")]
    public Task<string> LocalCheckStatus(CancellationToken cancellationToken = default)
    {
        if (_CheckoutOfCaller() is not { } caller)
        {
            return Task.FromResult(_CannotIdentifyCaller());
        }

        var (_, checkout) = caller;
        var jobs = _JobsIn(checkout)
            .Select(entry => new
            {
                workflow = Path.GetFileName(entry.WorkflowPath),
                job = entry.Verdict.JobId,
                canRunHere = entry.Verdict.CanRunLocally,
                reason = entry.Verdict.Reason,
            })
            .ToList();

        var last = tracker.LastFor(checkout);
        return Task.FromResult(McpJson.Of(new
        {
            ok = true,
            checkout,
            jobs,
            lastRun = last is null
                ? null
                : new
                {
                    job = last.Result.JobId,
                    verdict = last.Result.Outcome.ToString(),
                    summary = last.Result.Headline,
                    commit = last.Commit,
                    at = last.FinishedAt,
                },
        }));
    }

    private (string PaneId, string Checkout)? _CheckoutOfCaller() =>
        host.CurrentMcpCallerPaneId is { Length: > 0 } paneId && checkouts.CheckoutFor(paneId) is { } checkout
            ? (paneId, checkout)
            : null;

    private static string _CannotIdentifyCaller() =>
        McpJson.Error(
            "This tool runs the checks of the session that calls it, and the cockpit could not say which session "
            + "that is — or that session has not said which directory it is working in yet. There is no way to "
            + "name a project instead: that is deliberate.");

    private async Task<bool> _AskOperatorAsync(string command, string checkout, string paneId)
    {
        // AC-710: the operator's own opt-out, set on this plugin's settings page — off by default. On, this still
        // runs whatever the workflow says; the operator has chosen to approve that once instead of every run.
        if (settings.SkipConsent)
        {
            return true;
        }

        var decision = await host.RequestConsentAsync(new ConsentRequest(
            Title: "Local CI wants to run a workflow job on this machine",
            Action: $"{command}\n\nin {checkout}",
            Source: new ConsentSource(paneId, PluginId: null, "Local CI"),
            Scope: "local-ci.run",

            // Running a workflow job runs whatever the repository's own steps say to run, in a container with this
            // machine's Docker. That is code execution on the operator's say-so, so it is asked afresh every time.
            Risk: ConsentRisk.Dangerous));

        return decision.IsApproved;
    }

    private LocalRunRequest? _Choose(string checkout, string? job)
    {
        var runnable = _JobsIn(checkout).Where(entry => entry.Verdict.CanRunLocally);
        if (job is { Length: > 0 })
        {
            runnable = runnable.Where(entry => string.Equals(entry.Verdict.JobId, job, StringComparison.Ordinal));
        }

        // Cast so an empty sequence answers null rather than a default tuple, which would read as a real choice.
        return runnable.Cast<(string WorkflowPath, JobVerdict Verdict)?>().FirstOrDefault() is { } chosen
            ? new LocalRunRequest(checkout, chosen.WorkflowPath, chosen.Verdict.JobId)
            : null;
    }

    private static IEnumerable<(string WorkflowPath, JobVerdict Verdict)> _JobsIn(string checkout) =>
        WorkflowCatalog.ReadProject(checkout)
            .Where(read => read.Document is not null)
            .SelectMany(read => LocalRunClassifier.Classify(read.Document!).Select(verdict => (read.Path, verdict)));

    // What goes back to the agent. The log travels only when the run failed, and only its tail: a whole build log
    // in a session's context is the waste this plugin exists to save, and on a pass there is nothing in it to read.
    private static object _Report(LocalRunResult result) => new
    {
        ok = result.Outcome == LocalRunOutcome.Passed,
        reachedAVerdict = result.ReachedAVerdict,
        workflow = Path.GetFileName(result.WorkflowPath),
        job = result.JobId,
        verdict = result.Outcome.ToString(),
        seconds = Math.Round(result.Duration.TotalSeconds, 1),
        summary = result.Headline,
        where = "this machine, in a container via act",
        note = "act's images are not GitHub's runner images. This predicts the check on GitHub; it does not replace it.",
        // A failure, and also a run that fell over in its own setup (AC-617) — the latter needs it most: "it never
        // got past setting the job up" is the classification, and the engine's own message underneath it is the
        // only thing that says what to fix. Still never on a pass: a whole build log in a session's context is the
        // waste this plugin exists to save, and there is nothing in a green one to read.
        logTail = result.Outcome is LocalRunOutcome.Failed or LocalRunOutcome.CouldNotRun && result.LogTail.Length > 0
            ? result.LogTail
            : null,
    };
}

internal static class McpJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Of(object value) => JsonSerializer.Serialize(value, Options);

    public static string Error(string message) => Of(new { ok = false, error = message });
}
