using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The executeStep adapter behind the run-driver (AC-174): per step it embeds an agent session, awaits its
// done-report, has the still-live CEO validate the result, and returns pass/fail. The driver's own loop is tested
// separately; here it is the coordination and the pane gates (which pane may report done, validate, or raise a blockade).
[Collection("avalonia")]
public class AutopilotRunCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ReportStepDone_FromAPaneThatIsNotAnActiveStep_IsRejected()
    {
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), new AutopilotPlanController());

        Assert.False(coordinator.ReportStepDone("nobody", "done"));
    }

    [Fact]
    public void ReportValidation_WithNoValidationPending_OrTheWrongPane_IsRejected()
    {
        var plan = new AutopilotPlanController();
        plan.BindSession("ceo-pane");
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), plan);

        Assert.False(coordinator.ReportValidation("ceo-pane", passed: true, reason: null));
        Assert.False(coordinator.ReportValidation("intruder", passed: true, reason: null));
    }

    [Fact]
    public async Task RunAsync_StepReportsDone_CeoValidatesPass_SettlesMergeReady()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var stepSession = _Session("step-pane");
        var context = _Context(stepSession);
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "opened PR #1"));

        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "meets acceptance"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
        await stepSession.Received(1).CloseAsync();
    }

    [Fact]
    public async Task RunAsync_MergeReady_WhenPublishingThePullRequestFails_RecordsPullRequestMissing()
    {
        // AC-347 FIX B: a merge-ready run that could not deliver its PR (gh present, push worked, but opening the PR
        // failed) still needs a human to open it by hand — it must not read back as a clean settle.
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [_HardStep("1")]) { DeliversPullRequest = true });
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());

        var host = _Host();
        var stepSession = _Session("step-pane");
        var context = _Context(stepSession);
        // A hand-rolled fake, not an NSubstitute mock: NSubstitute cannot proxy an internal interface without the
        // assembly opting in to DynamicProxyGenAssembly2, which this project does not.
        var publisher = new FailingPrPublisher();
        var coordinator = new AutopilotRunCoordinator(host, plan, prPublisher: publisher);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var environment = new AutopilotRunEnvironment(
            "/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run", RunId: "run-1", RunLabel: "run");
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "opened PR #1"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "meets acceptance"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
        Assert.True(plan.PullRequestMissing);
    }

    [Fact]
    public async Task RunAsync_CeoValidatesFail_WithNoAttemptsLeft_SettlesBlocked()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "tried but it does not compile"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: false, reason: "does not meet acceptance"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.Blocked, plan.Phase);
        // The CEO's reason is surfaced on the step, so a failed step says why it was not accepted.
        Assert.Contains("does not meet acceptance", plan.Plan!.Steps[0].Note);
    }

    [Fact]
    public async Task RunAsync_CeoSessionEndsBeforeValidating_FailsTheStep_RatherThanHangingOnAVerdictThatCannotCome()
    {
        // AC-191. The host refuses to start a CEO whose provider does not vouch it confines its file tools, so the
        // validation turn is sent to a pane that is already gone and dropped in silence. Waiting on the verdict
        // alone would hang the run with nothing on screen — the step must fail with the host's own reason instead.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var ceoEnded = new TaskCompletionSource<string?>();
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane", ceoEnded.Task), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        // No verdict will ever arrive: the CEO's session ended with the host's refusal instead.
        ceoEnded.TrySetResult("Could not confine this run: the \"kimi\" profile does not confine its file tools to its working directory.");

        // The Timeout is the assertion: before this, the run waited here forever.
        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.Blocked, plan.Phase);
        Assert.Contains("does not confine its file tools", plan.Plan!.Steps[0].Note);
    }

    [Fact]
    public async Task RunAsync_CeoValidates_ThenItsSessionEnds_KeepsTheVerdictItAlreadyGave()
    {
        // The race the guard above must not lose: a CEO that answers and then ends. Its verdict is the real outcome —
        // reading the ending first would throw away a validation the run already earned and fail a passing step.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var ceoEnded = new TaskCompletionSource<string?>();
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane", ceoEnded.Task), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "opened PR #1"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: null));
        ceoEnded.TrySetResult("the workspace closed");

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    [Fact]
    public async Task RunAsync_EmbedsEachStepWithItsComposerDisabled()
    {
        // AC-174: a step agent drives itself, so its session starts with input off (the operator intervenes explicitly).
        var plan = _RunningPlan(_HardStep("1"));
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request => request.StartWithInputDisabled && request.IsolateInWorktree));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_NamesTheRunOnEveryStepItEmbeds_SoTheRunsSpendAddsUp()
    {
        // AC-251: a run gets a fresh session per step, which is exactly why its spend cannot be read off any one
        // of them. Each request carries the run, so the host's usage trail can group them back into one figure.
        var plan = _RunningPlan(_HardStep("1"));
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var environment = new AutopilotRunEnvironment("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run", RunId: "run-7", RunLabel: "AC-251 - persist usage");
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request =>
            request.RunId == "run-7" && request.RunLabel == "AC-251 - persist usage"));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForANonGitFolder_EmbedsTheStepUnisolatedInTheWorkingDirectory()
    {
        // AC-174: a run whose folder the host reported is not a git repository runs its steps
        // without worktree isolation, directly in that folder — an admin task with no repo, not refused at the first step.
        var plan = _RunningPlan(_HardStep("1"));
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var environment = new AutopilotRunEnvironment("/plain/folder", null, IsolateSteps: false);
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        // Not isolated in a worktree, but its file tools are confined to the working folder (least-privilege: a local
        // model without an OS sandbox is held to the operator's folder, not their home).
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request =>
            !request.IsolateInWorktree && request.WorkingDirectory == "/plain/folder" && request.ConfineFileToolsToWorkingDirectory));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForANonGitFolder_ForcesASingleAgent_EvenWhenTheStepAsksForMore()
    {
        // A non-git run has no per-agent worktree isolation, so a parallel step would race N agents on the same folder;
        // it is clamped to one agent (an isolated run keeps the split — each agent gets its own worktree).
        var plan = _RunningPlan(_ParallelStep("1", agents: 3));
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var environment = new AutopilotRunEnvironment("/plain/folder", null, IsolateSteps: false);
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        // Only one agent session is embedded despite the step asking for three.
        context.Received(1).EmbedSession(Arg.Any<EmbeddedSessionRequest>());

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task EnableCurrentStepInput_EnablesTheComposerOnTheLiveStepSession()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var stepSession = _Session("step-pane");
        var context = _Context(stepSession);
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        coordinator.EnableCurrentStepInput();
        stepSession.Received(1).SetInputEnabled(true);

        // Let the step settle cleanly so the run finishes.
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public void EnableCurrentStepInput_WithNoStepRunning_IsANoOp()
    {
        var coordinator = new AutopilotRunCoordinator(_Host(), new AutopilotPlanController());

        coordinator.EnableCurrentStepInput();
    }

    [Fact]
    public async Task RunAsync_StepSessionEndsBeforeReportingDone_FailsTheStep_SettlesBlocked()
    {
        // AC-174 fail-closed: the host ends an embedded step session it will not isolate (a non-confining provider)
        // before the agent ever reports done. The coordinator must treat that as a failed step — with no attempts left,
        // the run settles Blocked — rather than wait forever on a done-report that never comes.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var ended = new TaskCompletionSource<string?>();
        var context = _Context(_Session("step-pane", ended.Task));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        // The step session ends with a reason (the fail-closed refusal in the host) before it ever reports done.
        ended.TrySetResult("Could not isolate this run: the Qwen (local) profile's provider does not confine its file tools to the worktree.");

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.Blocked, plan.Phase);
        // The step never reported done, so the CEO is never asked to validate it.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());
        // The failure reason is surfaced on the step so it is not a silent red dot.
        Assert.Contains("does not confine its file tools to the worktree", plan.Plan!.Steps[0].Note);
    }

    [Fact]
    public async Task RunAsync_AStepSessionThatEndsBeforeReporting_ThenASuccessfulRetry_CountsTheAttemptButNotARework()
    {
        // AC-347 FIX J: the coordinator's fault path — a session the host refused to isolate, ending before it ever
        // reports done — must return Faulted, not Rejected: no CEO ever saw the work. A passing retry must then show
        // Attempts == 2 but Reworks == 0, proving the distinction holds through the real coordinator wiring.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var ended = new TaskCompletionSource<string?>();
        var firstAttemptSession = _Session("step-pane-1", ended.Task);
        var secondAttemptSession = _Session("step-pane-2");
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(firstAttemptSession, secondAttemptSession);
        context.Sessions.Returns(Substitute.For<ICockpitSessionObserver>());
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shownCount = 0;
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(
            context, _Session("ceo-pane"), _Settings(maxAttempts: 2),
            _ => Interlocked.Increment(ref shownCount), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await _Until(() => shownCount >= 1);
        // First attempt: the session ends before ever reporting done — the general catch turns this into Faulted.
        ended.TrySetResult("Could not isolate this run: the Qwen (local) profile's provider does not confine its file tools to the worktree.");

        await _Until(() => shownCount >= 2);
        // Second attempt (a fresh session): report done normally and have the CEO accept it.
        Assert.True(coordinator.ReportStepDone("step-pane-2", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
        var step = plan.Plan!.Steps[0];
        Assert.Equal(2, step.Attempts);
        Assert.Equal(0, step.Reworks);
    }

    [Fact]
    public async Task RunAsync_StepNeverReportsDone_StallDeadlineElapses_FailsTheStep_SettlesBlocked()
    {
        // AC-192: a step agent that keeps its session live but never reports done used to hang the whole run after
        // its one nudge — the wait was unbounded. With a hard stall deadline the step fails and the run settles
        // Blocked. Short reminder/stall values are injected so the test does not actually wait minutes.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(
            host,
            plan,
            stepDoneReminderDelay: TimeSpan.FromMilliseconds(20),
            stepStallTimeout: TimeSpan.FromMilliseconds(80));

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        await run.WaitAsync(Timeout);

        Assert.Equal(AutopilotPlanPhase.Blocked, plan.Phase);
        // The agent got its single nudge before the stall deadline gave up on it.
        await host.Received().SendToSessionAsync("step-pane", Arg.Any<string>());
        // The failed step explains itself as stalled rather than a silent red dot.
        Assert.Contains("stalled", plan.Plan!.Steps[0].Note);
        // A step that never reported is never handed to the CEO to validate.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_StepMakesToolProgress_IsNotStalled_EvenPastTheStallWindow()
    {
        // A step that is slow because it is working hard keeps resetting the stall window through its tool
        // activity, so it is never failed as stalled (unlike AC-192's silent agent). Timing-based: the 30ms progress
        // gap is well under the 100ms stall window (so each reset lands), while the total span is past it.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var stepSession = new ProgressingSession("step-pane");
        var context = _Context(stepSession);
        var coordinator = new AutopilotRunCoordinator(
            host,
            plan,
            stepDoneReminderDelay: TimeSpan.FromMilliseconds(15),
            stepStallTimeout: TimeSpan.FromMilliseconds(100));

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);

        // Steady progress across ~180ms — far past the 100ms stall window, but each 30ms gap resets it.
        for (var i = 0; i < 6; i++)
        {
            stepSession.RaiseActivity();
            await Task.Delay(30);
        }

        // Never failed as stalled: it reports done and reaches the CEO's validation, and the run settles merge-ready.
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.DoesNotContain("stalled", plan.Plan!.Steps[0].Note);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    [Fact]
    public async Task ReportValidation_AfterABlockadeLeftRunning_IsRejected_UntilTheRunResumes()
    {
        // AC-207: after AC-201 a blockade no longer comes from the CEO — it is a worker's consult the CEO escalates
        // to the operator. This exercises the validate-after-block race guard: during the validation window a
        // consult is escalated, moving the run off Running, so the pending validate must not resolve mid-blockade.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await _Until(() => ceoSends >= 1); // the validation turn reached the CEO — a validation is now pending

        // A worker consult during the validation window is escalated to the operator, moving the run to AwaitingOperator
        // while the validation is still pending.
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "one more question")));
        await _Until(() => ceoSends >= 2); // the consult reached the CEO
        Assert.True(coordinator.EscalateToOperator("ceo-pane", "operator, please decide"));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);
        Assert.False(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        // Once answered and running again, the pending validation resolves as normal and the run settles.
        await coordinator.AnswerBlockadeAsync("go ahead");
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));
        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    [Fact]
    public async Task AnswerBlockadeAsync_WithABlankAnswer_StillSendsAContinueTurnToTheWorker_AndResumes()
    {
        // AC-206: the blocked worker already ended its turn when it raised the blockade. A blank operator answer must
        // still send a turn (a minimal "Continue.") so the worker actually carries on, instead of only resuming the phase
        // and stranding the worker until the stall deadline.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        // Park the run on the operator through the live mechanism: a worker consults, the CEO escalates it to the operator.
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "need a decision")));
        await _Until(() => ceoSends >= 1);
        Assert.True(coordinator.EscalateToOperator("ceo-pane", "operator, please decide"));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);

        // A blank operator answer still relays a "Continue." turn to the worker's session and resumes the run.
        await coordinator.AnswerBlockadeAsync("   ");
        await host.Received(1).SendToSessionAsync("step-pane", "Continue.");
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AnswerBlockadeAsync_AfterTheRunSettled_DoesNotReviveIt()
    {
        var plan = _RunningPlan(_HardStep("1"));
        plan.SettleStep("1", AutopilotStepStatus.Passed);
        plan.Settle();
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);

        var coordinator = new AutopilotRunCoordinator(_Host(), plan);
        await coordinator.AnswerBlockadeAsync("a stray click after the run is done");

        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    [Fact]
    public async Task ReportTrackerStageAsync_FromTheCeo_ForASourceRun_MovesTheIssueOnItsTracker()
    {
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "Do it"), _HardStep("1"));
        var provider = Substitute.For<ITrackerProvider>();
        provider.TrackerId.Returns("youtrack");
        provider.SetStageAsync("AC-1", "Review", Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        var host = _Host();
        host.TrackerProviders.Returns(new[] { provider });
        var coordinator = new AutopilotRunCoordinator(host, plan);

        Assert.True((await coordinator.ReportTrackerStageAsync("ceo-pane", "Review")));
        await provider.Received(1).SetStageAsync("AC-1", "Review", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportTrackerStageAsync_FromANonCeoPane_IsRejected_AndDoesNotTouchTheTracker()
    {
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "t"), _HardStep("1"));
        var provider = Substitute.For<ITrackerProvider>();
        provider.TrackerId.Returns("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new[] { provider });
        var coordinator = new AutopilotRunCoordinator(host, plan);

        Assert.False((await coordinator.ReportTrackerStageAsync("intruder", "Review")));
        await provider.DidNotReceive().SetStageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportTrackerNoteAsync_ForACeoFirstRun_WithNoSource_IsRejected()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var provider = Substitute.For<ITrackerProvider>();
        provider.TrackerId.Returns("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new[] { provider });
        var coordinator = new AutopilotRunCoordinator(host, plan);

        Assert.False((await coordinator.ReportTrackerNoteAsync("ceo-pane", "evidence")));
        await provider.DidNotReceive().PostCommentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // AC-202 automatic phase→stage mapping: the coordinator itself moves the source issue as the run crosses a lifecycle
    // edge (start → in-progress, merge-ready → review), so the stage no longer hangs on the CEO calling autopilot_tracker_stage.

    [Fact]
    public async Task RunAsync_ForASourceRun_MovesTheIssueToDevelopAtStart_AndReviewAtMergeReady()
    {
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "Do it"), _HardStep("1"));
        var provider = new FakeTrackerProvider("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new ITrackerProvider[] { provider });
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        // The run moved the issue to Develop the moment it started — before any step reports, so it never sits on Backlog.
        Assert.Equal(("AC-1", "Develop"), Assert.Single(provider.StageCalls));

        Assert.True(coordinator.ReportStepDone("step-pane", "opened PR"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
        // Merge-ready moved it to Review (the tracker's own vocabulary via SuggestStageName) — the work is done, the
        // merge is left to the human, so it is not closed to Done automatically.
        Assert.Equal(new[] { ("AC-1", "Develop"), ("AC-1", "Review") }, provider.StageCalls);
    }

    [Fact]
    public async Task RunAsync_ForACeoFirstRun_WithNoSource_SetsNoStage()
    {
        // A CEO-first run has no tracker issue, so the auto-mapping must never touch a tracker even when one is installed.
        var plan = _RunningPlan(_HardStep("1"));
        var provider = new FakeTrackerProvider("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new ITrackerProvider[] { provider });
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
        Assert.Empty(provider.StageCalls);
    }

    [Fact]
    public async Task AutoAdvanceTrackerStage_IsIdempotent_DoesNotSetTheSameStageTwice()
    {
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "t"), _HardStep("1"));
        var provider = new FakeTrackerProvider("youtrack");
        var host = _Host();
        host.TrackerProviders.Returns(new ITrackerProvider[] { provider });
        var coordinator = new AutopilotRunCoordinator(host, plan);

        await coordinator.AutoAdvanceTrackerStageAsync(TrackerWorkStage.InProgress);
        await coordinator.AutoAdvanceTrackerStageAsync(TrackerWorkStage.InProgress);

        // The same lifecycle edge fired twice sets the stage once — a re-render or a retried edge does not re-move it.
        Assert.Equal(("AC-1", "Develop"), Assert.Single(provider.StageCalls));
    }

    [Fact]
    public async Task RunAsync_WhenTheTrackerThrows_TheRunStillSettlesMergeReady()
    {
        // Fail-soft: a tracker that throws (API down, no permission) must never take the run down — it settles as usual.
        var plan = _RunningPlanWithSource(new AutopilotPlanSource("youtrack", "AC-1", "t"), _HardStep("1"));
        var provider = new FakeTrackerProvider("youtrack", throwOnSet: true);
        var host = _Host();
        host.TrackerProviders.Returns(new ITrackerProvider[] { provider });
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportStepDone("step-pane", "done"));
        await validationSent.Task.WaitAsync(Timeout);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        // The run completes without faulting despite the tracker throwing on every stage move.
        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    // A concrete tracker provider that records SetStageAsync calls (a substitute cannot intercept SuggestStageName — it
    // is a default interface method), mapping the neutral stages to the AC board's own vocabulary like YouTrack does.
    private sealed class FakeTrackerProvider(string trackerId, bool throwOnSet = false) : ITrackerProvider
    {
        public string TrackerId => trackerId;

        public List<(string IssueId, string Stage)> StageCalls { get; } = [];

        public string? SuggestStageName(TrackerWorkStage stage) => stage switch
        {
            TrackerWorkStage.InProgress => "Develop",
            TrackerWorkStage.InReview => "Review",
            TrackerWorkStage.Done => "Done",
            _ => null,
        };

        public Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default)
        {
            if (throwOnSet)
            {
                throw new InvalidOperationException("tracker down");
            }

            lock (StageCalls)
            {
                StageCalls.Add((issueId, stage));
            }

            return Task.FromResult(true);
        }

        public Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TrackerComment>>([]);
    }

    // AC-201 tiered blocker escalation: a worker's autopilot_blocked routes to ReportConsultAsync, which consults the run's
    // CEO first (spoor 2) instead of the operator; the CEO answers (spoor 2 done) or escalates to the operator (spoor 3).

    [Fact]
    public async Task ReportConsult_DuringAStep_RelaysToTheCeo_AndLeavesTheRunRunning()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        // The worker consults its manager — the question is relayed into the CEO session and the run stays Running (a
        // consult is not an operator blockade).
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "Which database should it use?")));
        await _Until(() => ceoSends >= 1);
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);

        // Only one consult may be open at a time — a second, while the first is unanswered, is turned down.
        Assert.False((await coordinator.ReportConsultAsync("step-pane", "and another?")));
        // A pane that is not a live step worker cannot consult.
        Assert.False((await coordinator.ReportConsultAsync("intruder", "let me in")));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AnswerWorker_AfterAConsult_RelaysTheAnswerToTheWorker_AndClearsTheConsult()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        Assert.True((await coordinator.ReportConsultAsync("step-pane", "Which db?")));
        await _Until(() => ceoSends >= 1);

        // Only the run's CEO session answers a consult — an intruder cannot.
        Assert.False((await coordinator.AnswerWorkerAsync("intruder", "not you")));

        // The CEO's answer is relayed into the worker's session as a turn; the phase never left Running.
        Assert.True((await coordinator.AnswerWorkerAsync("ceo-pane", "Use Postgres.")));
        await host.Received(1).SendToSessionAsync("step-pane", "Use Postgres.");
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);

        // The consult is cleared: a second answer with none pending is rejected.
        Assert.False((await coordinator.AnswerWorkerAsync("ceo-pane", "again?")));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task EscalateToOperator_AfterAConsult_BlocksOnTheWorker_ThenTheOperatorAnswerReachesTheWorker()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        Assert.True((await coordinator.ReportConsultAsync("step-pane", "Need a prod credential.")));
        await _Until(() => ceoSends >= 1);

        // Only the CEO session escalates a consult.
        Assert.False(coordinator.EscalateToOperator("intruder", "nope"));

        // The CEO decides it is genuinely the operator's call: the run parks on the operator, and the pending pane is the
        // WORKER (not the CEO), so the operator's answer is later relayed to the worker through the unchanged AnswerBlockadeAsync.
        Assert.True(coordinator.EscalateToOperator("ceo-pane", "The step needs a production credential."));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);
        Assert.Equal("The step needs a production credential.", plan.PendingQuestion);

        await coordinator.AnswerBlockadeAsync("Here is the credential: XYZ.");
        await host.Received(1).SendToSessionAsync("step-pane", "Here is the credential: XYZ.");
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReportConsult_WithTheCeoSessionEnded_FailsClosedToTheOperator_WithoutRelayingToTheCeo()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        // The CEO session has already ended (its Completion has fired) — there is no live manager to consult.
        var endedCeo = _Session("ceo-pane", Task.FromResult<string?>("the CEO session ended"));
        var run = coordinator.RunAsync(context, endedCeo, _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, cts.Token);
        await shown.Task.WaitAsync(Timeout);

        // Fail-closed: with no live CEO the consult goes straight to the operator instead of being dropped.
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "Which db?")));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);
        Assert.Equal("Which db?", plan.PendingQuestion);
        // Nothing was relayed to the (ended) CEO session.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ReportConsult_OverTheStepConsultCap_FallsBackToTheOperator_AndTheCapResetsPerStep()
    {
        // Loop-cap (MaxConsultsPerStep = 1): the first consult of a step reaches the CEO; the next exceeds the step's
        // budget and falls back to the operator. The budget then resets for the next step — a fresh consult there reaches
        // the CEO again rather than being capped on the previous step's count.
        var plan = _RunningPlanSteps(_HardStep("1"), _HardStep("2"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var embeds = 0;
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1, maxConsults: 1), _ => Interlocked.Increment(ref embeds), _ => { }, _Env(), _DirectUi, cts.Token);

        // Step 1: first consult reaches the CEO (count 1, not over the cap of 1).
        await _Until(() => embeds >= 1);
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "q1")));
        await _Until(() => ceoSends >= 1);
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);
        Assert.True((await coordinator.AnswerWorkerAsync("ceo-pane", "a1")));

        // Step 1: the second consult exceeds the cap → it goes to the operator, not the CEO (ceoSends stays 1).
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "q2")));
        Assert.Equal(AutopilotPlanPhase.AwaitingOperator, plan.Phase);
        Assert.Equal("q2", plan.PendingQuestion);
        Assert.Equal(1, ceoSends);

        // The operator answers (relayed to the worker), which then finishes step 1 and the CEO validates it.
        await coordinator.AnswerBlockadeAsync("operator says X");
        Assert.True(coordinator.ReportStepDone("step-pane", "done 1"));
        await _Until(() => ceoSends >= 2); // the validation turn was sent to the CEO
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        // Step 2 starts with a fresh consult budget: its first consult reaches the CEO again (proving the per-step reset —
        // without it the count would still be over the cap and this would go to the operator).
        await _Until(() => embeds >= 2);
        Assert.True((await coordinator.ReportConsultAsync("step-pane", "q3")));
        await _Until(() => ceoSends >= 3);
        Assert.Equal(AutopilotPlanPhase.Running, plan.Phase);

        // Finish step 2 cleanly so the run settles.
        Assert.True((await coordinator.AnswerWorkerAsync("ceo-pane", "a3")));
        Assert.True(coordinator.ReportStepDone("step-pane", "done 2"));
        await _Until(() => ceoSends >= 4);
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok"));

        await run.WaitAsync(Timeout);
        Assert.Equal(AutopilotPlanPhase.MergeReady, plan.Phase);
    }

    [Fact]
    public async Task RunAsync_ForAReviewGateStep_ForksAFreshWorktreeOffTheRunBranch_InsteadOfTheSharedOne()
    {
        // AC-434: a review-gate step never writes to the run's shared worktree — it forks its own throwaway copy
        // off the run's branch tip, so concurrent gates never collide with each other or the later fix step.
        // WorktreePath null (fresh worktree) + WorkingDirectory the run branch is exactly that request shape.
        var plan = _RunningPlan(_HardStep("1") with { IsReviewGate = true });
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var environment = new AutopilotRunEnvironment("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run");
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request =>
            request.WorktreePath == null && request.IsolateInWorktree && request.WorkingDirectory == "/repo/.worktrees/run"));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForAnOrdinaryStep_StillUsesTheSharedRunWorktree_EvenWhenOneExists()
    {
        // The AC-434 change is scoped to IsReviewGate — an ordinary hard step keeps accumulating its work on the run's
        // one shared worktree exactly as before.
        var plan = _RunningPlan(_HardStep("1"));
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(_Host(), plan);

        var shown = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var environment = new AutopilotRunEnvironment("/repo", "/repo/.worktrees/run", IsolateSteps: true, RunWorktreeBranch: "autopilot/run");
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, environment, _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request =>
            request.WorktreePath == "/repo/.worktrees/run" && request.WorkingDirectory == "/repo"));

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task RunAsync_ForAReviewGroup_SerializesCeoValidation_SoEachGateSettlesOnItsOwnVerdict()
    {
        // Adversarial-review fix (AC-434): two gates' agent work runs concurrently, but the coordinator holds
        // exactly one CEO-validation slot (_validationGate) — each gate must settle on the verdict actually given
        // for IT. Before the fix, both gates shared one TaskCompletionSource, letting one overwrite the other's turn.
        var stepA = _HardStep("gate-a") with { Title = "Gate A", IsReviewGate = true };
        var stepB = _HardStep("gate-b") with { Title = "Gate B", IsReviewGate = true };
        var plan = _RunningPlanSteps(stepA, stepB);
        var host = _Host();
        var sessionA = _Session("gate-a-pane");
        var sessionB = _Session("gate-b-pane");
        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Is<EmbeddedSessionRequest>(request => request.InitialUserMessage!.Contains("Gate A"))).Returns(sessionA);
        context.EmbedSession(Arg.Is<EmbeddedSessionRequest>(request => request.InitialUserMessage!.Contains("Gate B"))).Returns(sessionB);
        context.Sessions.Returns(Substitute.For<ICockpitSessionObserver>());
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shownCount = 0;
        var validationTurns = new List<string>();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>()))
            .Do(call => { lock (validationTurns) { validationTurns.Add(call.ArgAt<string>(1)); } });

        // maxAttempts: 1 — a rejected gate settles Failed immediately (no rework), so this test isolates the
        // validation-routing question from the shared-fix-step machinery covered elsewhere.
        var run = coordinator.RunAsync(
            context, _Session("ceo-pane"), _Settings(maxAttempts: 1),
            _ => Interlocked.Increment(ref shownCount), _ => { }, _Env(), _DirectUi, CancellationToken.None);

        await _Until(() => shownCount >= 2);
        Assert.True(coordinator.ReportStepDone("gate-a-pane", "gate a done"));
        Assert.True(coordinator.ReportStepDone("gate-b-pane", "gate b done"));

        await _Until(() => validationTurns.Count >= 1);
        // The proof of serialization: with both gates' work already reported done, a second (unserialized) coordinator
        // would have sent both validation turns by now. Only one has gone out.
        Assert.Single(validationTurns);
        var firstWasGateA = validationTurns[0].Contains("Gate A");
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: true, reason: "clean"));

        await _Until(() => validationTurns.Count >= 2);
        Assert.NotEqual(firstWasGateA, validationTurns[1].Contains("Gate A"));
        Assert.True(coordinator.ReportValidation("ceo-pane", passed: false, reason: "found something"));

        await run.WaitAsync(Timeout);

        var gateAStatus = plan.Plan!.Steps.First(step => step.Id == "gate-a").Status;
        var gateBStatus = plan.Plan!.Steps.First(step => step.Id == "gate-b").Status;
        var (passedStatus, rejectedStatus) = firstWasGateA ? (gateAStatus, gateBStatus) : (gateBStatus, gateAStatus);
        Assert.Equal(AutopilotStepStatus.Passed, passedStatus);
        Assert.Equal(AutopilotStepStatus.Failed, rejectedStatus);
    }

    private static AutopilotPlanController _RunningPlan(AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [step]));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotPlanController _RunningPlanSteps(params AutopilotStep[] steps)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, steps));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotPlanController _RunningPlanWithSource(AutopilotPlanSource source, AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", source, [step]));
        plan.BindSession("ceo-pane");
        Assert.True(plan.Approve());
        return plan;
    }

    private static AutopilotStep _HardStep(string id) =>
        new(id, "Code", "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard);

    private static AutopilotStep _ParallelStep(string id, int agents) =>
        new(id, "Code", "do the work", "Claude", "opus", "brief", "compiles", GateMode.Hard) { AgentCount = agents };

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

    // A fake publisher that probes as a fully capable git+gh run but fails to open the pull request itself — the AC-347
    // FIX B scenario: the branch pushes, but the PR never lands, so the run must not read back as clean.
    private sealed class FailingPrPublisher : IAutopilotPrPublisher
    {
        public Task<AutopilotPrProbe> ProbeAsync(string worktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrProbe(IsGitRun: true, HasRemote: true, GhAvailable: true));

        public Task<AutopilotPrPublishResult> PublishAsync(AutopilotPrRequest request, bool createPullRequest, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AutopilotPrPublishResult(Pushed: true, PrUrl: null, Error: "gh failed to open the pull request"));

        public Task<bool> EnsureCommittedAsync(string worktreePath, string message, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<AutopilotStrayCommits> RecoverStrayCommitsAsync(string runWorktreePath, string runBranch, string stepWorktreePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(AutopilotStrayCommits.None);
    }

    // A hand-rolled step session whose Activity event can be raised on demand — NSubstitute cannot reliably raise an
    // interface event that carries a default (no-op) add/remove body, which IEmbeddedSession.Activity does.
    private sealed class ProgressingSession : IEmbeddedSession
    {
        private readonly TaskCompletionSource<string?> _completion = new();

        public ProgressingSession(string paneId) => PaneId = paneId;

        public Control View { get; } = new TextBlock();

        public string PaneId { get; }

        public Task<string?> Completion => _completion.Task;

        public event Action? Activity;

        public void RaiseActivity() => Activity?.Invoke();

        public Task CloseAsync()
        {
            _completion.TrySetResult(null);
            return Task.CompletedTask;
        }

        public void SetInputEnabled(bool enabled)
        {
        }
    }

    private static IEmbeddedSession _Session(string paneId, Task<string?>? completion = null)
    {
        var session = Substitute.For<IEmbeddedSession>();
        session.View.Returns(new TextBlock());
        session.PaneId.Returns(paneId);
        session.CloseAsync().Returns(Task.CompletedTask);
        // A live session's Completion has not fired; a never-completing task models that, so the coordinator waits on
        // the step's done-report as usual. A test that wants to model a session ending early passes its own task.
        session.Completion.Returns(completion ?? new TaskCompletionSource<string?>().Task);
        return session;
    }

    private static AutopilotSettings _Settings(int? maxAttempts = null, int? maxConsults = null)
    {
        var storage = Substitute.For<IPluginStorage>();
        if (maxAttempts is { } cap)
        {
            storage.Get<int?>("maxSelfFixAttempts").Returns(cap);
        }

        if (maxConsults is { } consultCap)
        {
            storage.Get<int?>("maxConsultsPerStep").Returns(consultCap);
        }

        return new AutopilotSettings(storage);
    }

    private static async Task _Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "the condition should hold within the timeout");
    }

    private static AutopilotRunEnvironment _Env(bool isolate = true) => new("/repo", null, isolate);

    private static Task _DirectUi(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
