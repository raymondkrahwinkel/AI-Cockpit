using System.Reflection;
using System.Text.Json;
using Cockpit.Core.Worktrees;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Mcp;
using Cockpit.Plugin.LocalCi.Sessions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.LocalCi.Tests;

public class LocalCiMcpToolsTests : IDisposable
{
    private readonly TemporaryProject _caller = new();
    private readonly TemporaryProject _neighbour = new();
    private readonly FakeCockpitHost _host = new();
    private readonly SessionCheckouts _checkouts = new();
    private readonly LocalRunTracker _tracker = new();

    public LocalCiMcpToolsTests()
    {
        _caller.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        _neighbour.AddWorkflow("ci.yml", TemporaryProject.OneLinuxJob);
        _checkouts.Remember(new FakeSession("pane-caller", _caller.Root));
        _checkouts.Remember(new FakeSession("pane-neighbour", _neighbour.Root));
    }

    public void Dispose()
    {
        _caller.Dispose();
        _neighbour.Dispose();
    }

    [Fact]
    public void NeitherToolTakesAProjectOrAPathFromItsCaller()
    {
        // The point of the whole design: a session cannot ask for a run in another session's tree, and a
        // prompt-injected one cannot be talked into naming somewhere else. Pinned on the signature, because that is
        // where such a parameter would quietly reappear.
        foreach (var tool in typeof(LocalCiMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var names = tool.GetParameters().Select(parameter => parameter.Name!).ToList();

            Assert.DoesNotContain(names, name => name.Contains("path", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("project", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("directory", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("session", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task TheRunHappensInTheCallersCheckoutAndNotAnybodyElses()
    {
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act).RunLocalChecks();

        Assert.Equal(_caller.Root, act.Calls.Single().WorkingDirectory);
    }

    [Fact]
    public async Task ACheckoutTheCallerRegisteredForItselfViaWorktreeCreateCanBeTested()
    {
        // AC-1015: a subtask worktree an agent made for itself never gets its own pane, so it can only be reached
        // by naming it — but only because it is registered to this same caller.
        _host.CallerPaneId = "pane-caller";
        var worktrees = new FakeWorktreeManager();
        worktrees.Records.Add(new WorktreeRecord("pane-caller", _caller.Root, _neighbour.Root, "cockpit/subtask", "abc123", DateTimeOffset.UtcNow));
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act, worktrees: worktrees).RunLocalChecks(checkout: _neighbour.Root);

        Assert.Equal(_neighbour.Root, act.Calls.Single().WorkingDirectory);
    }

    [Fact]
    public async Task ACheckoutTheCallerDoesNotOwnIsRefused()
    {
        _host.CallerPaneId = "pane-caller";
        // Registered to a different pane — the exact confused-deputy case worktree_remove already guards against.
        var worktrees = new FakeWorktreeManager();
        worktrees.Records.Add(new WorktreeRecord("pane-neighbour", _neighbour.Root, _neighbour.Root, "cockpit/other", "abc123", DateTimeOffset.UtcNow));
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act, worktrees: worktrees).RunLocalChecks(checkout: _neighbour.Root);

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ACheckoutThatIsNotAnyRegisteredWorktreeIsRefused()
    {
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act, worktrees: new FakeWorktreeManager()).RunLocalChecks(checkout: _neighbour.Root);

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task WithoutAKnownCallerNothingRuns()
    {
        _host.CallerPaneId = null;
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task AnEmptyCallerIsNoCallerAtAll()
    {
        _host.CallerPaneId = string.Empty;
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        // The blank is the interesting one: a host that has no answer and a host that answers with nothing look
        // alike here, and treating the second as a real caller is how a lookup starts being done on an id nobody
        // vouched for.
        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ACallerTheCockpitDoesNotKnowGetsNothing()
    {
        _host.CallerPaneId = "pane-that-never-registered";
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task TheOperatorIsAskedWithTheLiteralCommandAndDenyingItStopsTheRun()
    {
        _host.CallerPaneId = "pane-caller";
        _host.Answer = ConsentOutcome.Denied;
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks();

        var asked = Assert.Single(_host.Asked);
        Assert.StartsWith("act ", asked.Action);
        Assert.Contains(_caller.Root, asked.Action);
        Assert.Equal(ConsentRisk.Dangerous, asked.Risk);
        Assert.Empty(act.Calls);
        Assert.Equal(nameof(LocalRunOutcome.NotApproved), _Read(answer).GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task DefaultSettingsStillAskEveryRun()
    {
        // AC-710: the setting exists, but a fresh install must keep asking every time — nothing about today's
        // behaviour changes until the operator opts in.
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        await _Tools(act, skipConsent: false).RunLocalChecks();

        Assert.Single(_host.Asked);
        Assert.Single(act.Calls);
    }

    [Fact]
    public async Task SkipConsentRunsWithoutAskingTheOperator()
    {
        // AC-710: with the operator's opt-out on, the run goes straight through — no RequestConsentAsync round-trip.
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = _Read(await _Tools(act, skipConsent: true).RunLocalChecks());

        Assert.Empty(_host.Asked);
        Assert.Single(act.Calls);
        Assert.True(answer.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task APassSaysWhereItRanAndCarriesNoLog()
    {
        _host.CallerPaneId = "pane-caller";
        var answer = _Read(await _Tools(FakeStreamingCliRunner.Exiting(0, "restore", "Job succeeded")).RunLocalChecks());

        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("this machine", answer.GetProperty("where").GetString());

        // A whole build log in a session's context is the waste this feature exists to save, and a passing run has
        // nothing in it worth reading.
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("logTail").ValueKind);
    }

    [Fact]
    public async Task AFailureCarriesTheEndOfTheLogWhereTheFailingTestIs()
    {
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(1, "restore", "  Failed ThisFailsOnPurpose [1 ms]", "Failed! - Failed: 1, Passed: 112");

        var answer = _Read(await _Tools(act).RunLocalChecks());

        Assert.False(answer.GetProperty("ok").GetBoolean());
        Assert.Contains("ThisFailsOnPurpose", answer.GetProperty("logTail").GetString());
    }

    [Fact]
    public async Task NoAnswerEverReadsAsAStatementAboutCi()
    {
        _host.CallerPaneId = "pane-caller";
        var answer = _Read(await _Tools(FakeStreamingCliRunner.Exiting(0)).RunLocalChecks());

        Assert.Contains("does not replace it", answer.GetProperty("note").GetString());
        Assert.DoesNotContain("CI", answer.GetProperty("summary").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AJobThatCannotRunHereIsNotSubstitutedForOneThatCan()
    {
        _caller.AddWorkflow("ci.yml", TemporaryProject.MatrixJob);
        _host.CallerPaneId = "pane-caller";
        var act = FakeStreamingCliRunner.Exiting(0);

        var answer = await _Tools(act).RunLocalChecks("spread");

        // Quietly running something else would be the worst possible answer: the agent asked about one job and
        // would be told about another.
        Assert.False(_Read(answer).GetProperty("ok").GetBoolean());
        Assert.Empty(act.Calls);
    }

    [Fact]
    public async Task ADroppedConnectionDoesNotAbortTheRunOnlyKillDoes()
    {
        // AC-1053: a transport that drops mid-run used to cancel the run itself via a linked cancellationToken
        // and discard the work. It must keep running; only Kill (tracker's registered stopAsync) may stop it.
        _host.CallerPaneId = "pane-caller";
        var running = new TaskCompletionSource();
        var act = FakeStreamingCliRunner.Blocking(running);
        var tools = _Tools(act);
        using var request = new CancellationTokenSource();

        var runTask = tools.RunLocalChecks(cancellationToken: request.Token);
        await running.Task;

        request.Cancel();
        await Task.Delay(50);
        Assert.Null(_tracker.LastFor(_caller.Root));

        await _tracker.Snapshot().Single().StopAsync();
        var answer = _Read(await runTask);

        Assert.Equal(nameof(LocalRunOutcome.Cancelled), answer.GetProperty("verdict").GetString());
        Assert.Equal(nameof(LocalRunOutcome.Cancelled), _tracker.LastFor(_caller.Root)!.Result.Outcome.ToString());
    }

    [Fact]
    public async Task TheStatusListsEveryJobWithItsReasonAndTheLastRun()
    {
        _caller.AddWorkflow("nightly.yml", TemporaryProject.MatrixJob);
        _host.CallerPaneId = "pane-caller";
        var tools = _Tools(FakeStreamingCliRunner.Exiting(0));
        await tools.RunLocalChecks();

        var status = _Read(await tools.LocalCheckStatus());

        var jobs = status.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Contains(jobs, job => job.GetProperty("job").GetString() == "build" && job.GetProperty("canRunHere").GetBoolean());
        Assert.Contains(jobs, job => job.GetProperty("job").GetString() == "spread" && !job.GetProperty("canRunHere").GetBoolean());
        Assert.Equal("build", status.GetProperty("lastRun").GetProperty("job").GetString());
    }

    private LocalCiMcpTools _Tools(IStreamingCliRunner act, bool skipConsent = false, FakeWorktreeManager? worktrees = null)
    {
        var runner = new LocalJobRunner(
            FakeLocalCiRuntime.Ready(), act, new FakeRunContainerCleanup(), () => ActRunOptions.For(8), () => "run-1");
        var settings = new LocalCiSettings(_host.Storage) { SkipConsent = skipConsent };

        return new LocalCiMcpTools(_host, _checkouts, runner, _tracker, new GitHead(new FakeCliRunner()), settings, worktrees);
    }

    private static JsonElement _Read(string json) => JsonDocument.Parse(json).RootElement;
}
