using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// Who decides that a step is validated cheaply (AC-255). The harness does: it offers the CEO its own account of a
/// step only where it could actually observe one, and hands it the unchanged deep-inspection instruction everywhere
/// else. Neither the CEO nor the step agent has any say in that — which is the point, since the step agent is the party
/// being checked. The wording of both turns is tested in <see cref="AutopilotStepEvidenceTests"/>.
/// </summary>
[Collection("avalonia")]
public class AutopilotEvidenceGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_ForAStepInTheRunsSharedWorktree_ValidatesAgainstTheHarnessEvidence()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], "@@ -1 +1 @@\n+new", false));
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("What the harness itself observed", turns[0]);
        Assert.Contains("src/Thing.cs", turns[0]);
        Assert.DoesNotContain("do not rely on the summary alone", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_MeasuresTheStepAgainstTheWorktreeAsItStood_BeforeTheStepRan()
    {
        // "What this step changed" is a difference between two moments, so the mark is taken before the agent is
        // embedded — measuring against the worktree afterwards would compare it to its own result.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], "diff", false));
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        // The step's session is on screen, so the mark was already taken — the agent has not reported anything yet.
        await shown.Task.WaitAsync(Timeout);
        Assert.Equal(["/repo/.worktrees/run"], source.Marked);

        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);
        var (collectedPath, collectedMark) = Assert.Single(source.Collected);
        Assert.Equal("/repo/.worktrees/run", collectedPath);
        Assert.Equal("mark-commit", collectedMark.Commit);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForANonGitFolder_NeverAsksForEvidence_AndKeepsTheInspectionInstruction()
    {
        // The branch is the harness's own call, taken off what it knows about the run (AC-174's IsolateSteps): an admin
        // task in a plain folder has no worktree to observe, so the CEO keeps validating exactly as it did before.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], "diff", false));
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, new AutopilotRunEnvironment("/plain/folder", null, IsolateSteps: false), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "filed the invoices"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("do not rely on the summary alone", turns[0]);
        Assert.DoesNotContain("What the harness itself observed", turns[0]);
        Assert.Empty(source.Marked);
        Assert.Empty(source.Collected);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WhenTheHarnessCannotObserveTheWorktree_KeepsTheInspectionInstruction()
    {
        // A git worktree that git itself would not answer about (a failed probe) must not read as "nothing changed" —
        // "we could not look" and "the step changed nothing" lead to opposite instructions, so it falls back.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var source = new FakeEvidenceSource(change: null);
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("do not rely on the summary alone", turns[0]);
        Assert.DoesNotContain("What the harness itself observed", turns[0]);
        // It did try — the fallback is the answer to a failed observation, not a decision to skip observing.
        Assert.NotEmpty(source.Collected);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForAReviewGateStep_KeepsTheInspectionInstruction()
    {
        // A review gate does not write to the shared worktree at all (AC-434: it forks its own throwaway copy to read),
        // and its deliverable is a judgement rather than a change — there is nothing for the harness to diff.
        var plan = _RunningPlan(_HardStep("1") with { IsReviewGate = true });
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], "diff", false));
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "reviewed it, found nothing"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("do not rely on the summary alone", turns[0]);
        Assert.Empty(source.Marked);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WithNoEvidenceSourceAtAll_ValidatesExactlyAsItDidBefore()
    {
        // The gate is additive: a coordinator built without a source (a bare graph, an older wiring) behaves as before.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var coordinator = new AutopilotRunCoordinator(host, plan);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("do not rely on the summary alone", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WhenTheEvidenceSourceThrows_StillValidatesTheStep()
    {
        // Collecting evidence exists to make validation cheaper; it may never be the reason a step fails.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: new ThrowingEvidenceSource());
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("do not rely on the summary alone", turns[0]);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    // A hand-rolled fake, not an NSubstitute mock: NSubstitute cannot proxy an internal interface without the assembly
    // opting in to DynamicProxyGenAssembly2, which this project does not.
    private sealed class FakeEvidenceSource(AutopilotWorktreeChange? change) : IAutopilotEvidenceSource
    {
        public List<string> Marked { get; } = [];

        public List<(string Path, AutopilotWorktreeMark Mark)> Collected { get; } = [];

        public Task<AutopilotWorktreeMark?> MarkAsync(string worktreePath, CancellationToken cancellationToken = default)
        {
            lock (Marked)
            {
                Marked.Add(worktreePath);
            }

            return Task.FromResult<AutopilotWorktreeMark?>(new AutopilotWorktreeMark("mark-commit", []));
        }

        public Task<AutopilotWorktreeChange?> CollectAsync(string worktreePath, AutopilotWorktreeMark mark, CancellationToken cancellationToken = default)
        {
            lock (Collected)
            {
                Collected.Add((worktreePath, mark));
            }

            return Task.FromResult(change);
        }
    }

    private sealed class ThrowingEvidenceSource : IAutopilotEvidenceSource
    {
        public Task<AutopilotWorktreeMark?> MarkAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");

        public Task<AutopilotWorktreeChange?> CollectAsync(string worktreePath, AutopilotWorktreeMark mark, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");
    }

    private static List<string> _CaptureCeoTurns(ICockpitHost host)
    {
        var turns = new List<string>();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>()))
            .Do(call => { lock (turns) { turns.Add(call.ArgAt<string>(1)); } });
        return turns;
    }

    private static AutopilotRunEnvironment _WorktreeEnvironment() =>
        new("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run");

    private static AutopilotPlanController _RunningPlan(AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [step]));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotStep _HardStep(string id) =>
        new(id, "Code", "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard);

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

    private static AutopilotSettings _Settings() => new(Substitute.For<IPluginStorage>());

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
