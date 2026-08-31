using System.ComponentModel;
using System.Text.Json;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugin.LocalCi.Workflows;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.LocalCi.Mcp;

// The `cockpit-local-ci` tool surface: how a session checks its own work before pushing it.
//
// Neither tool takes a project or an arbitrary path — `checkout` only picks among worktrees the caller's own pane
// already owns (AC-1015). Which checkout is acted on by default comes from `ICockpitHost.CurrentMcpCallerPaneId` —
// the pane the transport says made the call — so a prompt-injected one cannot be talked into somebody else's tree.
internal sealed class LocalCiMcpTools(
    ICockpitHost host,
    SessionCheckouts checkouts,
    ILocalJobRunner runner,
    LocalRunTracker tracker,
    GitHead head,
    LocalCiSettings settings,
    IWorktreeManager? worktrees = null)
{
    [McpServerTool(Name = "run_local_checks")]
    [Description(
        "Runs one of this project's GitHub workflow jobs in a container on this machine, using this session's own "
        + "checkout, and returns the verdict. The project is the calling session's — this tool takes no arbitrary "
        + "path and will not run anywhere else, but see `checkout` below for the one exception. A job that cannot "
        + "run locally (a complex matrix, a non-Linux runner, artifacts exchanged with another job) is refused with the "
        + "reason rather than partly run. The operator is asked to approve the exact command before anything "
        + "starts. The verdict is about this machine: act's images are not GitHub's, so it predicts the "
        + "pull-request check and does not replace it. `AlreadyRunning` is a verdict, not something to retry: "
        + "another local run already has this machine, it is not stuck, and calling this again will not change "
        + "that — try again later, or ask the operator to stop it. A long run keeps going even if this call never "
        + "returns an answer (a dropped connection does not stop it) — if that happens, call local_check_status "
        + "instead of running this again; it has the verdict this call could not deliver.")]
    public async Task<string> RunLocalChecks(
        [Description("The job to run, named as it is in the workflow (e.g. \"build\"). Leave it out to run the first job in this project that can run here.")]
        string? job = null,
        [Description(
            "The worktree to test, when it is not this session's own checkout — for example one this session made "
            + "for a subtask with worktree_create. Must be a path from worktree_list that this session owns; "
            + "anything else is refused. Leave it out to test this session's own checkout.")]
        string? checkout = null,
        CancellationToken cancellationToken = default)
    {
        if (_CheckoutOfCaller() is not { } caller)
        {
            return _CannotIdentifyCaller();
        }

        var (paneId, ownCheckout) = caller;
        if (await _ResolveCheckoutAsync(paneId, ownCheckout, checkout, cancellationToken) is not { } resolved)
        {
            return _CannotUseThatCheckout();
        }

        if (_Choose(resolved, job) is not { } chosen)
        {
            return McpJson.Error(job is { Length: > 0 }
                ? $"No job called {job} in this project can run on this machine. Ask for local_check_status to see what can."
                : "No job in this project's workflows can run on this machine.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var commit = await head.ReadAsync(resolved, cancellationToken);

        // AC-1053: not linked to `cancellationToken` — that tracks the request/transport, not operator intent.
        // Only the Kill button (tracker.Begin's stopAsync below) may cancel `stopping` now; a run also outlives
        // a client that died mid-call, on purpose — losing finished work is worse than `act` running unsupervised.
        using var stopping = new CancellationTokenSource();

        tracker.Begin(resolved, chosen.JobId, startedAt, () =>
        {
            stopping.Cancel();
            return Task.CompletedTask;
        });

        try
        {
            var result = await runner.RunAsync(
                chosen,
                _ => { },
                command => _AskOperatorAsync(command, resolved, paneId),
                stopping.Token);

            tracker.Complete(resolved, result, commit, DateTimeOffset.UtcNow);
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
                resolved,
                LocalRunResult.DidNotRun(chosen.WorkflowPath, chosen.JobId, outcome, reason),
                commit,
                DateTimeOffset.UtcNow);
            throw;
        }
    }

    [McpServerTool(Name = "local_check_status")]
    [Description(
        "Reports what this project's workflow jobs are — which of them can run on this machine and, for each that "
        + "cannot, why — plus the last local run in that checkout and whether it was on the commit that is checked "
        + "out now. The project is the calling session's own checkout by default; pass `checkout` for a worktree "
        + "this session made for itself with worktree_create instead. This is also the recovery path when "
        + "run_local_checks itself never returned an answer: the run kept going regardless, so `lastRun` here "
        + "carries its verdict without needing to run it again.")]
    public async Task<string> LocalCheckStatus(
        [Description(
            "The worktree to report on, when it is not this session's own checkout. Must be a path from "
            + "worktree_list that this session owns; anything else is refused. Leave it out for this session's own checkout.")]
        string? checkout = null,
        CancellationToken cancellationToken = default)
    {
        if (_CheckoutOfCaller() is not { } caller)
        {
            return _CannotIdentifyCaller();
        }

        var (paneId, ownCheckout) = caller;
        if (await _ResolveCheckoutAsync(paneId, ownCheckout, checkout, cancellationToken) is not { } resolved)
        {
            return _CannotUseThatCheckout();
        }

        var jobs = _JobsIn(resolved)
            .Select(entry => new
            {
                workflow = Path.GetFileName(entry.WorkflowPath),
                job = entry.Verdict.JobId,
                canRunHere = entry.Verdict.CanRunLocally,
                reason = entry.Verdict.Reason,
            })
            .ToList();

        var last = tracker.LastFor(resolved);
        return McpJson.Of(new
        {
            ok = true,
            checkout = resolved,
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
        });
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

    private static string _CannotUseThatCheckout() =>
        McpJson.Error(
            "That is not a worktree this session owns — call worktree_list to see the paths it may use, or leave "
            + "checkout out to use this session's own.");

    // AC-1015: the only path a caller may name is a worktree `worktree_create` already registered to this same
    // pane — a subtask worktree has no pane of its own to be reached through otherwise. Anything else is refused:
    // naming a path is not the same as owning it.
    private async Task<string?> _ResolveCheckoutAsync(
        string paneId, string ownCheckout, string? requested, CancellationToken cancellationToken)
    {
        if (requested is not { Length: > 0 })
        {
            return ownCheckout;
        }

        if (worktrees is null)
        {
            return null;
        }

        var full = Path.GetFullPath(requested);
        var owned = (await worktrees.ListAsync(cancellationToken))
            .Any(record => string.Equals(record.SessionId, paneId, StringComparison.Ordinal)
                && string.Equals(Path.GetFullPath(record.Path), full, _PathComparison));

        return owned ? full : null;
    }

    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
