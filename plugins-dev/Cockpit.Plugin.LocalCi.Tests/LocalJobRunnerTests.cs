using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalJobRunnerTests : IDisposable
{
    private readonly TemporaryProject _project = new();
    private readonly FakeRunContainerCleanup _cleanup = new();

    public void Dispose() => _project.Dispose();

    [Fact]
    public async Task WithoutDockerNothingIsStartedAndTheDetectionSpeaks()
    {
        var act = FakeStreamingCliRunner.Exiting(0);
        var result = await _RunAsync(FakeLocalCiRuntime.WithoutDocker(), act, TemporaryProject.OneLinuxJob, "build");

        Assert.Equal(LocalRunOutcome.CouldNotRun, result.Outcome);
        Assert.Equal(DockerRuntimeStatus.NotInstalled.Message, result.Reason);
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task WithoutActNothingIsStartedAndTheDetectionSpeaks()
    {
        var act = FakeStreamingCliRunner.Exiting(0);
        var result = await _RunAsync(FakeLocalCiRuntime.WithoutAct(), act, TemporaryProject.OneLinuxJob, "build");

        Assert.Equal(LocalRunOutcome.CouldNotRun, result.Outcome);
        Assert.Equal(ActRuntimeStatus.NotInstalled.Message, result.Reason);
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ARefusedJobIsNeverHandedToAct()
    {
        var act = FakeStreamingCliRunner.Exiting(0);
        var result = await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.MatrixJob, "spread");

        Assert.Equal(LocalRunOutcome.Refused, result.Outcome);
        Assert.Contains("matrix", result.Reason);

        // The whole point of the rule: a job we will not run whole is not run at all.
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task AZeroExitIsAPassAndTheRunHappensInTheCheckout()
    {
        var act = FakeStreamingCliRunner.Exiting(0, "Job succeeded");
        var result = await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.OneLinuxJob, "build");

        Assert.Equal(LocalRunOutcome.Passed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(("act", _project.Root), (act.Calls.Single().FileName, act.Calls.Single().WorkingDirectory));
    }

    [Fact]
    public async Task ANonZeroExitIsAFailureAndKeepsTheEndOfTheLog()
    {
        var act = FakeStreamingCliRunner.Exiting(1, "restore", "Failed! - Failed: 1, Passed: 40");
        var result = await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.OneLinuxJob, "build");

        Assert.Equal(LocalRunOutcome.Failed, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Failed: 1", result.LogTail);
    }

    [Fact]
    public async Task ARunThatDiedSettingItselfUpIsNotReportedAsAFailedBuild()
    {
        // AC-617: Docker 29.7.0 refused to copy an action into the container, so every job on the machine went red
        // in seconds without compiling a line. Reported as "build failed", that sends the operator into a diff that
        // was never built — the reason it has to come back as an outcome that reached no verdict at all.
        var act = FakeStreamingCliRunner.Exiting(
            1,
            "[CI/build]   ✅  Success - Main actions/checkout@v7",
            "[CI/build]   ❌  Failure - Main actions/setup-dotnet@v6",
            "[CI/build] failed to copy content to container: Error response from daemon: path escapes from parent");

        var result = await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.JobWithSetupActions, "build");

        Assert.Equal(LocalRunOutcome.CouldNotRun, result.Outcome);
        Assert.False(result.ReachedAVerdict);
        Assert.Contains("actions/setup-dotnet@v6", result.Reason);

        // The engine's own words travel with it: the classification says whose problem it is, the log says what to fix.
        Assert.Contains("path escapes from parent", result.LogTail);
    }

    [Fact]
    public async Task AJobWhoseOwnStepFailsIsStillAFailure()
    {
        // The other half of AC-617, on the same workflow that has setup actions in it — so the distinction is doing
        // the work, rather than the test passing because there was nothing to confuse it with.
        var act = FakeStreamingCliRunner.Exiting(
            1,
            "[CI/build]   ✅  Success - Main actions/setup-dotnet@v6",
            "[CI/build] error CS0103: The name 'Foo' does not exist in the current context",
            "[CI/build]   ❌  Failure - Main Build");

        var result = await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.JobWithSetupActions, "build");

        Assert.Equal(LocalRunOutcome.Failed, result.Outcome);
        Assert.True(result.ReachedAVerdict);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task EveryLineReachesTheCallerWhileTheRunIsHappening()
    {
        var seen = new List<string>();
        var act = FakeStreamingCliRunner.Exiting(0, "one", "two", "three");

        await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.OneLinuxJob, "build", seen.Add);

        Assert.Equal(["one", "two", "three"], seen);
    }

    [Fact]
    public async Task ActVanishingBetweenTheCheckAndTheRunIsAnAnswerRatherThanACrash()
    {
        var result = await _RunAsync(
            FakeLocalCiRuntime.Ready(), FakeStreamingCliRunner.NeverStarts(), TemporaryProject.OneLinuxJob, "build");

        Assert.Equal(LocalRunOutcome.CouldNotRun, result.Outcome);
        Assert.Contains("still on PATH", result.Reason);
    }

    [Fact]
    public async Task ARunThatIsNotApprovedDoesNotHappen()
    {
        var act = FakeStreamingCliRunner.Exiting(0);

        var result = await _RunAsync(
            FakeLocalCiRuntime.Ready(), act, TemporaryProject.OneLinuxJob, "build", approve: _ => Task.FromResult(false));

        Assert.Equal(LocalRunOutcome.NotApproved, result.Outcome);
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task TheApprovalIsAskedWithTheCommandItselfBeforeAnythingStarts()
    {
        var asked = string.Empty;
        var act = FakeStreamingCliRunner.Exiting(0);

        await _RunAsync(FakeLocalCiRuntime.Ready(), act, TemporaryProject.OneLinuxJob, "build", approve: command =>
        {
            asked = command;
            return Task.FromResult(true);
        });

        // What the operator is shown has to be what runs — a summary of it is a gate that approves something else.
        Assert.StartsWith("act ", asked);
        Assert.Contains("-j build", asked);
        Assert.Contains(".github/workflows/ci.yml", asked);
        Assert.Single(act.Calls);
    }

    [Fact]
    public async Task StoppingARunLeavesNoContainersBehind()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        var running = new TaskCompletionSource();
        using var runner = _RunnerFor(FakeLocalCiRuntime.Ready(), FakeStreamingCliRunner.Blocking(running), "run-7");
        using var stopping = new CancellationTokenSource();

        var run = runner.RunAsync(new LocalRunRequest(_project.Root, workflow, "build"), _ => { }, approve: null, stopping.Token);
        await running.Task;
        await stopping.CancelAsync();
        var result = await run;

        Assert.Equal(LocalRunOutcome.Cancelled, result.Outcome);

        // act removes its own containers when it finishes; killed halfway it does not, and those containers hold
        // the cores the operator stopped the run to get back.
        Assert.Equal(["run-7"], _cleanup.Removed);
    }

    [Fact]
    public async Task AStopThatArrivesBeforeTheRunStartsIsAnswered_NotThrown()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        var act = FakeStreamingCliRunner.Exiting(0);
        using var runner = _RunnerFor(FakeLocalCiRuntime.Ready(), act, "run-1");
        using var alreadyStopped = new CancellationTokenSource();
        await alreadyStopped.CancelAsync();

        var result = await runner.RunAsync(
            new LocalRunRequest(_project.Root, workflow, "build"), _ => { }, approve: null, alreadyStopped.Token);

        // Everything before act starts observes the token, so without this the caller is handed an exception where
        // it was promised an answer — and whoever is showing the run never learns it ended.
        Assert.Equal(LocalRunOutcome.Cancelled, result.Outcome);
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ASecondRunIsToldTheMachineIsBusyRatherThanQueued()
    {
        var workflow = _project.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        var running = new TaskCompletionSource();
        var act = FakeStreamingCliRunner.Blocking(running);
        using var runner = _RunnerFor(FakeLocalCiRuntime.Ready(), act, "run-1");
        using var stopping = new CancellationTokenSource();

        var first = runner.RunAsync(new LocalRunRequest(_project.Root, workflow, "build"), _ => { }, approve: null, stopping.Token);
        await running.Task;
        var second = await runner.RunAsync(new LocalRunRequest(_project.Root, workflow, "build"), _ => { }, approve: null, CancellationToken.None);

        Assert.Equal(LocalRunOutcome.AlreadyRunning, second.Outcome);
        Assert.Single(act.Calls);

        // AC-1015: this sentence sent three sessions into a wait-and-retry loop in one night. It must never again
        // read as "wait for it" — a caller (often an LLM agent) treats that as an instruction to poll.
        Assert.DoesNotContain("wait for it", second.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not stuck", second.Reason);

        await stopping.CancelAsync();
        await first;
    }

    private LocalJobRunner _RunnerFor(ILocalCiRuntime runtime, IStreamingCliRunner act, string runId) =>
        new(runtime, act, _cleanup, () => ActRunOptions.For(8), () => runId);

    private async Task<LocalRunResult> _RunAsync(
        ILocalCiRuntime runtime,
        IStreamingCliRunner act,
        string yaml,
        string jobId,
        Action<string>? onLine = null,
        Func<string, Task<bool>>? approve = null)
    {
        var workflow = _project.AddWorkflow("ci.yml", yaml);
        using var runner = _RunnerFor(runtime, act, "run-1");
        return await runner.RunAsync(
            new LocalRunRequest(_project.Root, workflow, jobId), onLine ?? (_ => { }), approve, CancellationToken.None);
    }
}
