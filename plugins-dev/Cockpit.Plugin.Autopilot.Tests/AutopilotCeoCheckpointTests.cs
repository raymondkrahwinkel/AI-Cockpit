using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// AC-253: one long-lived validator carries every earlier step's diff, so each new validation turn re-reads the whole
// tail. A checkpoint replaces it with a fresh session that carries only a one-line verdict per settled step. The ledger
// is a pure builder, tested without a session; the swap itself is tested through the coordinator, since what matters is
// that the next step's turn — and its verdict — actually reach the new pane.
[Collection("avalonia")]
public class AutopilotCeoCheckpointTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void CarryOver_GivesEverySettledStep_OneLineWithItsVerdict()
    {
        var carryOver = AutopilotCeoCheckpoint.CarryOver(new AutopilotPlan("goal", null, [
            _Step("1", "Code it").WithStatus(AutopilotStepStatus.Passed),
            _Step("2", "Review it").WithStatus(AutopilotStepStatus.Failed).WithNote("CEO: no test for the error path"),
            _Step("3", "Publish it").WithStatus(AutopilotStepStatus.Skipped),
        ]));

        Assert.Contains("- Code it: done, verified", carryOver);
        Assert.Contains("- Review it: not accepted — CEO: no test for the error path", carryOver);
        Assert.Contains("- Publish it: skipped", carryOver);
    }

    [Fact]
    public void CarryOver_LeavesOutEveryStepNobodyHasJudgedYet()
    {
        var carryOver = AutopilotCeoCheckpoint.CarryOver(new AutopilotPlan("goal", null, [
            _Step("1", "Code it").WithStatus(AutopilotStepStatus.Passed),
            _Step("2", "Running now").WithStatus(AutopilotStepStatus.Running).WithNote("Work reported…"),
            _Step("3", "Still pending"),
            _Step("4", "Waiting on the operator").WithStatus(AutopilotStepStatus.Blocked),
        ]));

        Assert.Contains("- Code it: done, verified", carryOver);
        Assert.DoesNotContain("Running now", carryOver);
        Assert.DoesNotContain("Still pending", carryOver);
        Assert.DoesNotContain("Waiting on the operator", carryOver);
    }

    [Fact]
    public void CarryOver_SaysTheEarlierDiffsAreGone_SoTheCeoDoesNotReadSilenceAsAbsence()
    {
        var carryOver = AutopilotCeoCheckpoint.CarryOver(new AutopilotPlan("goal", null, [
            _Step("1", "Code it").WithStatus(AutopilotStepStatus.Passed),
        ]));

        Assert.Contains("NOT in this conversation", carryOver);
        Assert.Contains("git history", carryOver);
    }

    [Fact]
    public void CarryOver_FlattensAndCutsALongNote_SoTheLedgerStaysOneLinePerStep()
    {
        var note = "first line\nsecond line\n" + new string('x', 400);
        var carryOver = AutopilotCeoCheckpoint.CarryOver(new AutopilotPlan("goal", null, [
            _Step("1", "Code it").WithStatus(AutopilotStepStatus.Failed).WithNote(note),
        ]));

        var line = carryOver.Split('\n').Single(candidate => candidate.StartsWith("- Code it:", StringComparison.Ordinal));
        Assert.Contains("first line second line", line);
        Assert.EndsWith("…", line);
        Assert.True(line.Length < 220, $"the ledger line should stay short, was {line.Length}");
    }

    [Fact]
    public void IsDue_NeverFires_OnAnIntervalOfZero()
    {
        Assert.False(AutopilotCeoCheckpoint.IsDue(0, 0));
        Assert.False(AutopilotCeoCheckpoint.IsDue(9, 0));
    }

    [Fact]
    public void IsDue_FiresOnceTheValidatorHasTakenOnItsInterval()
    {
        Assert.False(AutopilotCeoCheckpoint.IsDue(2, 3));
        Assert.True(AutopilotCeoCheckpoint.IsDue(3, 3));
        Assert.True(AutopilotCeoCheckpoint.IsDue(4, 3));
    }

    [Fact]
    public void CeoCheckpointEverySteps_DefaultsToThree_ThenRoundTripsPerProject()
    {
        var settings = new AutopilotSettings(new FakeStorage());

        Assert.Equal(3, settings.CeoCheckpointEverySteps());

        settings.SetCeoCheckpointEverySteps(0);
        Assert.Equal(0, settings.CeoCheckpointEverySteps());

        const string project = "/home/me/repo";
        settings.SetCeoCheckpointEverySteps(5, project);
        Assert.Equal(5, settings.CeoCheckpointEverySteps(project));
        Assert.Equal(0, settings.CeoCheckpointEverySteps());
    }

    [Fact]
    public async Task RunAsync_OnceTheIntervalIsReached_SendsTheNextStepsValidationToAFreshCeo()
    {
        var plan = _RunningPlan(_Step("1", "Code it"), _Step("2", "Verify it"));
        var host = _Host();
        var turns = _CaptureTurns(host);
        var carriedOver = new List<string>();
        var coordinator = new AutopilotRunCoordinator(host, plan, checkpointCeo: carryOver =>
        {
            carriedOver.Add(carryOver);
            return Task.FromResult<IEmbeddedSession?>(_Session("ceo-2"));
        });

        var run = _Start(coordinator, host, _Settings(checkpointEvery: 1));

        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);

        // The second step's turn lands on the fresh pane, and its verdict from that pane is the one that settles the
        // run — the swap has to have rebound the run's session, or ReportValidation turns "ceo-2" down and this hangs.
        await _SettleStep(coordinator, turns, "ceo-2", passed: true);
        await run.WaitAsync(Timeout);

        // The fresh validator is briefed on what the one it replaced already judged, and nothing else of it.
        Assert.Single(carriedOver);
        Assert.Contains("- Code it: done, verified", carriedOver[0]);
        Assert.DoesNotContain("Verify it", carriedOver[0]);
    }

    [Fact]
    public async Task RunAsync_OnAnIntervalOfZero_KeepsTheOneValidatorItStartedWith()
    {
        var plan = _RunningPlan(_Step("1", "Code it"), _Step("2", "Verify it"));
        var host = _Host();
        var turns = _CaptureTurns(host);
        var checkpoints = 0;
        var coordinator = new AutopilotRunCoordinator(host, plan, checkpointCeo: _ =>
        {
            checkpoints++;
            return Task.FromResult<IEmbeddedSession?>(_Session("ceo-2"));
        });

        var run = _Start(coordinator, host, _Settings(checkpointEvery: 0));

        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);
        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);
        await run.WaitAsync(Timeout);

        Assert.Equal(0, checkpoints);
    }

    [Fact]
    public async Task RunAsync_WithAWorkerWaitingOnTheCeo_LeavesTheValidatorInPlace()
    {
        var plan = _RunningPlan(_Step("1", "Code it"), _Step("2", "Verify it"));
        var host = _Host();
        var turns = _CaptureTurns(host);
        var checkpoints = 0;
        var coordinator = new AutopilotRunCoordinator(host, plan, checkpointCeo: _ =>
        {
            checkpoints++;
            return Task.FromResult<IEmbeddedSession?>(_Session("ceo-2"));
        });

        var run = _Start(coordinator, host, _Settings(checkpointEvery: 1));

        // The first step settles as usual; the interval is reached, so the second step's validation is where the swap
        // would otherwise happen.
        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);

        // The second step's worker asked its manager something and is waiting for the answer in that exact session (AC-201).
        await _Until(() => plan.ActiveStep?.Id == "2");
        await _Once(() => coordinator.ReportConsultAsync("step-pane", "which convention applies here?"), "the worker should reach its manager");

        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);
        await run.WaitAsync(Timeout);

        // The consult was still open at the moment the checkpoint came due, so the swap that would have stranded it
        // never happened — and the validation turn stayed on the pane the worker is waiting on.
        Assert.Equal(0, checkpoints);
    }

    [Fact]
    public async Task RunAsync_WhenTheFreshSessionCannotBeEmbedded_CarriesOnWithTheValidatorItHas()
    {
        var plan = _RunningPlan(_Step("1", "Code it"), _Step("2", "Verify it"));
        var host = _Host();
        var turns = _CaptureTurns(host);
        var coordinator = new AutopilotRunCoordinator(host, plan, checkpointCeo: _ => Task.FromResult<IEmbeddedSession?>(null));

        var run = _Start(coordinator, host, _Settings(checkpointEvery: 1));

        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);
        await _SettleStep(coordinator, turns, "ceo-pane", passed: true);
        await run.WaitAsync(Timeout);

        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    // Runs one step to a settled verdict: report the work done, wait for the validation turn to land on `ceoPane`, and
    // answer it as that pane.
    private static async Task _SettleStep(AutopilotRunCoordinator coordinator, List<(string Pane, string Text)> turns, string ceoPane, bool passed)
    {
        var before = _Count(turns, ceoPane);
        await _Once(() => Task.FromResult(coordinator.ReportStepDone("step-pane", "did the work")), "the step's work should be reportable");
        await _Until(() => _Count(turns, ceoPane) > before);
        await _Once(() => Task.FromResult(coordinator.ReportValidation(ceoPane, passed, reason: "ok")), $"{ceoPane} should be the pane whose verdict counts");
    }

    // Retries a one-shot report (reporting done, consulting, answering a validation) until the run is ready for it. Kept
    // apart from `_Until`, which re-evaluates its condition for the assertion — a second call here would always fail.
    private static async Task _Once(Func<Task<bool>> attempt, string what)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await attempt())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail(what);
    }

    private static int _Count(List<(string Pane, string Text)> turns, string pane)
    {
        lock (turns)
        {
            return turns.Count(turn => turn.Pane == pane);
        }
    }

    private static Task _Start(AutopilotRunCoordinator coordinator, ICockpitHost host, AutopilotSettings settings) =>
        coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), settings,
            _ => { }, _ => { }, new AutopilotRunEnvironment("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run"), _DirectUi, CancellationToken.None);

    private static List<(string Pane, string Text)> _CaptureTurns(ICockpitHost host)
    {
        var turns = new List<(string Pane, string Text)>();
        host.When(h => h.SendToSessionAsync(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => { lock (turns) { turns.Add((call.ArgAt<string>(0), call.ArgAt<string>(1))); } });
        return turns;
    }

    private static AutopilotPlanController _RunningPlan(params AutopilotStep[] steps)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, steps));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotStep _Step(string id, string title) =>
        new(id, title, "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard);

    private static ICockpitHost _Host()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SendToSessionAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.CompletedTask);
        return host;
    }

    private static IWorkspaceContext _Context(IEmbeddedSession stepSession)
    {
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(stepSession);
        context.Sessions.Returns(Substitute.For<ICockpitSessionObserver>());
        return context;
    }

    private static IEmbeddedSession _Session(string paneId)
    {
        var session = Substitute.For<IEmbeddedSession>();
        session.View.Returns(new TextBlock());
        session.PaneId.Returns(paneId);
        session.CloseAsync().Returns(Task.CompletedTask);
        session.Completion.Returns(new TaskCompletionSource<string?>().Task);
        return session;
    }

    private static AutopilotSettings _Settings(int checkpointEvery)
    {
        var settings = new AutopilotSettings(new FakeStorage());
        settings.SetCeoCheckpointEverySteps(checkpointEvery);
        return settings;
    }

    // The host's storage round-trips through JSON, so a value written here reads back the way a real one would.
    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private static async Task _Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should hold within the timeout");
    }

    private static Task _DirectUi(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
