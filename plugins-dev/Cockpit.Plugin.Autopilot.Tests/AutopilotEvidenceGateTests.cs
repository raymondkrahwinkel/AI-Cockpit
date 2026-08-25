using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// Who decides that a step is validated cheaply (AC-255). The harness does: it offers the CEO its own account of a
// step only where it could actually observe one, else the unchanged deep-inspection instruction. Neither the CEO
// nor the step agent (the party being checked) has any say in that.
[Collection("avalonia")]
public class AutopilotEvidenceGateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_ForAStepInTheRunsSharedWorktree_ValidatesAgainstTheHarnessEvidence()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], [], "c0ffee1", "@@ -1 +1 @@\n+new", false));
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
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], [], "c0ffee1", "diff", false));
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
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], [], "c0ffee1", "diff", false));
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
    public async Task RunAsync_ForAReviewGateStep_MeasuresTheRunsOwnWorktree()
    {
        // AC-1037: a gate is handed no shared worktree (AC-434), and evidence used to hang off that — so the one step
        // type that works on a branch of its own was the one nothing was ever measured for. It is measured against the
        // run's worktree like every other step, which is where its work has to end up.
        var plan = _RunningPlan(_HardStep("1") with { IsReviewGate = true });
        var host = _Host();
        var source = new FakeEvidenceSource(new AutopilotWorktreeChange(["src/Thing.cs"], [], [], "c0ffee1", "diff", false));
        var coordinator = new AutopilotRunCoordinator(host, plan, evidenceSource: source);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "reviewed it, found nothing"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("What the harness itself observed", turns[0]);
        Assert.Equal(["/repo/.worktrees/run"], source.Marked);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForAStepThatRanInAWorktreeOfItsOwn_BringsItsCommitsOntoTheRunBranch()
    {
        // AC-1037: the step commits where it was started, which for a gate or a parallel agent is a branch of its own.
        // The harness fetches that work back before anyone judges the step, and says so.
        var plan = _RunningPlan(_HardStep("1") with { IsReviewGate = true });
        var host = _Host();
        var publisher = new FakePrPublisher(new AutopilotStrayCommits(["3885af2611", "2a2695461f"], [], null));
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: publisher);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane", "/repo/.worktrees/gate")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "fixed the six findings, 73/73 green"));
        await _Until(() => turns.Count >= 1);

        Assert.Equal([("/repo/.worktrees/run", "autopilot/run", "/repo/.worktrees/gate")], publisher.Recoveries);
        Assert.Contains("Cherry-picked 2 commit(s)", turns[0]);
        Assert.Contains("3885af26", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WhenAStrayCommitCannotBeBroughtBack_TellsTheCeoItIsNotInTheRun()
    {
        // A cherry-pick that hits a conflict stops there. The step still reported success, so the one thing that must
        // not happen is silence: the CEO is told the report describes a tree this run does not have.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var publisher = new FakePrPublisher(new AutopilotStrayCommits([], ["367b5e5a99"], "could not apply 367b5e5a"));
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: publisher);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane", "/repo/.worktrees/agent-2")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done, all tests pass"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("could NOT be brought over", turns[0]);
        Assert.Contains("could not apply 367b5e5a", turns[0]);
        Assert.Contains("Do not accept the step on that report.", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WhenTheStrayCommitCheckCouldNotRun_SaysSo_RatherThanLettingTheStepReadAsClean()
    {
        // AC-1037: a check that did not happen must not arrive as silence. The step reported success, so silence here
        // is exactly what would let it through — the failure shape this ticket exists to close, one branch further on.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var publisher = new FakePrPublisher(AutopilotStrayCommits.Unmeasured("fatal: bad revision"));
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: publisher);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane", "/repo/.worktrees/gate")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done, all green"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("could not check whether this step's work landed", turns[0]);
        Assert.Contains("fatal: bad revision", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_WhenTheStrayCommitCheckThrows_LandsWhereAReportedFailureLands()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: new ThrowingPrPublisher());
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane", "/repo/.worktrees/gate")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await _Until(() => turns.Count >= 1);

        Assert.Contains("could not check whether this step's work landed", turns[0]);
        Assert.Contains("git is not here", turns[0]);

        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForAStepInTheRunsOwnWorktree_SaysNothingAboutStrayCommits()
    {
        // The ordinary case: nothing was found off the branch, so nothing is said. A note here every step would make
        // the one step that needs it unreadable.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var publisher = new FakePrPublisher(AutopilotStrayCommits.None);
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: publisher);
        var turns = _CaptureCeoTurns(host);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(
            _Context(_Session("step-pane", "/repo/.worktrees/run")), _Session("ceo-pane"), _Settings(),
            _ => shown.TrySetResult(), _ => { }, _WorktreeEnvironment(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "did the work"));
        await _Until(() => turns.Count >= 1);

        Assert.DoesNotContain("branch of its own", turns[0]);

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

    // AC-1037: only the stray-commit recovery is asked of it here; the publishing half is exercised in its own suite.
    private sealed class FakePrPublisher(AutopilotStrayCommits stray) : IAutopilotPrPublisher
    {
        public List<(string RunWorktree, string RunBranch, string StepWorktree)> Recoveries { get; } = [];

        public Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrProbe(IsGitRun: true, HasRemote: true, GhAvailable: true));

        public Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrPublishResult(Pushed: true, PrUrl: "https://example.invalid/pr/1", Error: null));

        public Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(string runWorktreePath, string runBranch, string stepWorktreePath, CancellationToken cancellationToken = default)
        {
            lock (Recoveries)
            {
                Recoveries.Add((runWorktreePath, runBranch, stepWorktreePath));
            }

            return Task.FromResult(string.Equals(runWorktreePath, stepWorktreePath, StringComparison.Ordinal) ? AutopilotStrayCommits.None : stray);
        }
    }

    private sealed class ThrowingPrPublisher : IAutopilotPrPublisher
    {
        public Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");

        public Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");

        public Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");

        public Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(string runWorktreePath, string runBranch, string stepWorktreePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("git is not here");
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

    private static IEmbeddedSession _Session(string paneId, string? worktreePath = null)
    {
        var session = Substitute.For<IEmbeddedSession>();
        session.View.Returns(new TextBlock());
        session.PaneId.Returns(paneId);
        session.WorktreePath.Returns(worktreePath);
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
