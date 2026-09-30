using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Runtime;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalJobRunnerTests : IDisposable
{
    private readonly TemporaryProject _project = new();
    private readonly FakeRunContainerCleanup _cleanup = new();

    public void Dispose() => _project.Dispose();

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
