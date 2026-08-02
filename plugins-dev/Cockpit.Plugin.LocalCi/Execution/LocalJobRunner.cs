using System.Diagnostics;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>Runs one workflow job on this machine, or says why it did not.</summary>
internal interface ILocalJobRunner
{
    /// <summary>
    /// Runs <paramref name="request"/>, handing each line of output to <paramref name="onLine"/> as it arrives.
    /// Never throws for an ordinary refusal — a machine without Docker, a job that cannot run here, a run already
    /// in progress are all outcomes on the result, because a caller that has to catch an exception to learn "not
    /// run" is a caller that will one day treat it as "ran".
    /// </summary>
    /// <param name="approve">
    /// Asked with the literal command, once it is known and before anything starts, when the run needs the
    /// operator's say-so — an agent asking for one. Null when the operator is the one asking, which is the case
    /// when they pressed the button themselves. Answering no ends the run as
    /// <see cref="LocalRunOutcome.NotApproved"/>.
    /// </param>
    Task<LocalRunResult> RunAsync(
        LocalRunRequest request,
        Action<string> onLine,
        Func<string, Task<bool>>? approve,
        CancellationToken cancellationToken);
}

/// <summary>
/// The order a local run is decided in: is this machine able, is this job allowed, is the machine free — and only
/// then act. Each question is answered before the next is asked, so a refusal always names the first thing wrong
/// rather than whichever check happened to run last.
/// </summary>
internal sealed class LocalJobRunner(
    ILocalCiRuntime runtime,
    IStreamingCliRunner cli,
    IRunContainerCleanup cleanup,
    Func<ActRunOptions> options,
    Func<string> newRunId) : ILocalJobRunner, IDisposable
{
    // One at a time, and a second request is answered rather than queued. The machine is the operator's own and
    // several sessions are usually live; two container builds at once take the cockpit down with them. Queueing
    // would leave the asking session waiting on something it was never told about.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<LocalRunResult> RunAsync(
        LocalRunRequest request,
        Action<string> onLine,
        Func<string, Task<bool>>? approve,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _DecideAndRunAsync(request, onLine, approve, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A stop that lands before the run starts is still a stop, and it has to come back as one. Everything
            // up to here — probing the machine, reading the workflows, taking the slot — observes the token and
            // would otherwise throw out of a caller that was promised an answer, leaving whoever is showing the
            // run with no way to learn it ended.
            return LocalRunResult.DidNotRun(
                request.WorkflowPath,
                request.JobId,
                LocalRunOutcome.Cancelled,
                "the run was stopped before it started.");
        }
    }

    private async Task<LocalRunResult> _DecideAndRunAsync(
        LocalRunRequest request,
        Action<string> onLine,
        Func<string, Task<bool>>? approve,
        CancellationToken cancellationToken)
    {
        var status = await runtime.GetStatusAsync(cancellationToken);
        if (!status.CanRunJobs)
        {
            return LocalRunResult.DidNotRun(
                request.WorkflowPath, request.JobId, LocalRunOutcome.CouldNotRun, _WhyTheMachineCannot(status));
        }

        var approval = LocalRunApproval.For(request);
        if (approval is not { IsApproved: true, RunnerLabel: { } runnerLabel })
        {
            return LocalRunResult.DidNotRun(
                request.WorkflowPath, request.JobId, LocalRunOutcome.Refused, approval.Reason);
        }

        if (!await _oneAtATime.WaitAsync(0, cancellationToken))
        {
            return LocalRunResult.DidNotRun(
                request.WorkflowPath,
                request.JobId,
                LocalRunOutcome.AlreadyRunning,
                "another local run already has this machine. Wait for it, or stop it from the status bar.");
        }

        try
        {
            return await _RunApprovedAsync(request, runnerLabel, approval.SetupActions, onLine, approve, cancellationToken);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public void Dispose() => _oneAtATime.Dispose();

    private async Task<LocalRunResult> _RunApprovedAsync(
        LocalRunRequest request,
        string runnerLabel,
        IReadOnlyList<string> setupActions,
        Action<string> onLine,
        Func<string, Task<bool>>? approve,
        CancellationToken cancellationToken)
    {
        var runId = newRunId();
        var arguments = ActCommand.Build(request, runnerLabel, options(), runId);

        // Asked with the command itself, not with a sentence about it: what the operator sees is what will run.
        if (approve is not null && !await approve(ActCommand.Describe(arguments)))
        {
            return LocalRunResult.DidNotRun(
                request.WorkflowPath,
                request.JobId,
                LocalRunOutcome.NotApproved,
                "running it on this machine was not approved.");
        }

        var tail = LogTail.ForFailure();
        var elapsed = Stopwatch.StartNew();

        void Keep(string line)
        {
            tail.Add(line);
            onLine(line);
        }

        try
        {
            var run = await cli.RunAsync("act", arguments, request.ProjectRoot, Keep, cancellationToken);
            if (!run.Started)
            {
                return LocalRunResult.DidNotRun(
                    request.WorkflowPath,
                    request.JobId,
                    LocalRunOutcome.CouldNotRun,
                    "act answered when this machine was checked but could not be started now. Check that it is still on PATH.");
            }

            if (run.Succeeded)
            {
                return new LocalRunResult(
                    request.WorkflowPath, request.JobId, LocalRunOutcome.Passed, elapsed.Elapsed, run.ExitCode,
                    Reason: null, tail.Text());
            }

            // A failure is only a verdict on the code once the code was reached (AC-617). One that happened while
            // act was still setting the job up says something about this machine instead, and reporting it as
            // "failed" sends the operator hunting through a diff that was never compiled.
            var setupFailure = SetupFailure.Reason(tail.Lines(), setupActions);

            return new LocalRunResult(
                request.WorkflowPath,
                request.JobId,
                setupFailure is null ? LocalRunOutcome.Failed : LocalRunOutcome.CouldNotRun,
                elapsed.Elapsed,
                run.ExitCode,
                setupFailure,
                tail.Text());
        }
        catch (OperationCanceledException)
        {
            // Not the caller's token: the point of this call is to clean up after a cancellation, so passing the
            // token that just fired would cancel the cleanup too and leave the containers behind.
            await cleanup.RemoveAsync(runId, CancellationToken.None);

            return new LocalRunResult(
                request.WorkflowPath,
                request.JobId,
                LocalRunOutcome.Cancelled,
                elapsed.Elapsed,
                ExitCode: null,
                "the run was stopped before it reached a verdict.",
                tail.Text());
        }
    }

    /// <summary>The half that is not ready speaks for itself — those sentences are what the detection is for.</summary>
    private static string _WhyTheMachineCannot(LocalCiRuntimeStatus status) =>
        status.Docker.IsReady ? status.Act.Message : status.Docker.Message;
}
