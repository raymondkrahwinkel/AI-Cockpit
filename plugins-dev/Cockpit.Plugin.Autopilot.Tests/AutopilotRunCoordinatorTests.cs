using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The executeStep adapter behind the run-driver (AC-174): per step it embeds an agent session, awaits its
/// done-report, has the still-live CEO validate the result, and returns pass/fail. The MCP tools report through it, so
/// the coordinator also guards which pane may report done, validate, or raise a blockade. The driver's own loop is
/// tested separately; here it is the coordination and the pane gates.
/// </summary>
[Collection("avalonia")]
public class AutopilotRunCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ReportStepDone_FromAPaneThatIsNotAnActiveStep_IsRejected()
    {
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), new AutopilotPlanController());

        coordinator.ReportStepDone("nobody", "done").Should().BeFalse();
    }

    [Fact]
    public void ReportValidation_WithNoValidationPending_OrTheWrongPane_IsRejected()
    {
        var plan = new AutopilotPlanController();
        plan.BindSession("ceo-pane");
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), plan);

        coordinator.ReportValidation("ceo-pane", passed: true, reason: null).Should().BeFalse();
        coordinator.ReportValidation("intruder", passed: true, reason: null).Should().BeFalse();
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
        coordinator.ReportStepDone("step-pane", "opened PR #1").Should().BeTrue();

        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "meets acceptance").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
        await stepSession.Received(1).CloseAsync();
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
        coordinator.ReportStepDone("step-pane", "tried but it does not compile").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: false, reason: "does not meet acceptance").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        // The CEO's reason is surfaced on the step, so a failed step says why it was not accepted.
        plan.Plan!.Steps[0].Note.Should().Contain("does not meet acceptance");
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
    public async Task RunAsync_ForANonGitFolder_EmbedsTheStepUnisolatedInTheWorkingDirectory()
    {
        // AC-174 (Raymond 2026-07-22): a run whose folder the host reported is not a git repository runs its steps
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
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public void EnableCurrentStepInput_WithNoStepRunning_IsANoOp()
    {
        var coordinator = new AutopilotRunCoordinator(_Host(), new AutopilotPlanController());

        coordinator.Invoking(c => c.EnableCurrentStepInput()).Should().NotThrow();
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
        plan.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        // The step never reported done, so the CEO is never asked to validate it.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());
        // The failure reason is surfaced on the step so it is not a silent red dot.
        plan.Plan!.Steps[0].Note.Should().Contain("does not confine its file tools to the worktree");
    }

    [Fact]
    public async Task RunAsync_StepNeverReportsDone_StallDeadlineElapses_FailsTheStep_SettlesBlocked()
    {
        // AC-192: a step agent that keeps its session live but never reports done (a local model stuck emitting a text
        // tool-call it never runs) used to hang the whole run after its one nudge — the wait was unbounded. With a hard
        // stall deadline the step fails, and with no attempts left the run settles Blocked instead of hanging forever.
        // Short reminder/stall values are injected so the test does not actually wait minutes.
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

        plan.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        // The agent got its single nudge before the stall deadline gave up on it.
        await host.Received().SendToSessionAsync("step-pane", Arg.Any<string>());
        // The failed step explains itself as stalled rather than a silent red dot.
        plan.Plan!.Steps[0].Note.Should().Contain("stalled");
        // A step that never reported is never handed to the CEO to validate.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());
    }

    [Fact]
    public async Task RunAsync_StepMakesToolProgress_IsNotStalled_EvenPastTheStallWindow()
    {
        // Raymond 2026-07-23: a step that is slow because it is working hard — a long turn with many tool calls — keeps
        // resetting the stall window through its tool activity, so it is never failed as stalled (unlike AC-192, the
        // silent agent above). Tool progress is raised across a span well past the stall window; the step is then handed
        // to the CEO to validate rather than failed. Timing-based: the 30ms progress gap is well under the 100ms stall
        // window (so each reset lands), while the total span is past it (so only the reset keeps the step alive).
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
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        plan.Plan!.Steps[0].Note.Should().NotContain("stalled");
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
    }

    [Fact]
    public async Task ReportValidation_AfterABlockadeLeftRunning_IsRejected_UntilTheRunResumes()
    {
        // AC-207: after AC-201 a blockade no longer comes from the CEO — it is a worker's consult the CEO escalates to
        // the operator. This exercises the same validate-after-block race guard through that live mechanism: during the
        // validation window a consult is escalated, moving the run off Running, so the pending validate must not resolve
        // mid-blockade and corrupt the run.
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var ceoSends = 0;
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => Interlocked.Increment(ref ceoSends));

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _ => { }, _Env(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await _Until(() => ceoSends >= 1); // the validation turn reached the CEO — a validation is now pending

        // A worker consult during the validation window is escalated to the operator, moving the run to AwaitingOperator
        // while the validation is still pending.
        (await coordinator.ReportConsultAsync("step-pane", "one more question")).Should().BeTrue();
        await _Until(() => ceoSends >= 2); // the consult reached the CEO
        coordinator.EscalateToOperator("ceo-pane", "operator, please decide").Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeFalse();

        // Once answered and running again, the pending validation resolves as normal and the run settles.
        await coordinator.AnswerBlockadeAsync("go ahead");
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();
        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
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
        (await coordinator.ReportConsultAsync("step-pane", "need a decision")).Should().BeTrue();
        await _Until(() => ceoSends >= 1);
        coordinator.EscalateToOperator("ceo-pane", "operator, please decide").Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);

        // A blank operator answer still relays a "Continue." turn to the worker's session and resumes the run.
        await coordinator.AnswerBlockadeAsync("   ");
        await host.Received(1).SendToSessionAsync("step-pane", "Continue.");
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

        cts.Cancel();
        await run.WaitAsync(Timeout);
    }

    [Fact]
    public async Task AnswerBlockadeAsync_AfterTheRunSettled_DoesNotReviveIt()
    {
        var plan = _RunningPlan(_HardStep("1"));
        plan.SettleStep("1", AutopilotStepStatus.Passed);
        plan.Settle();
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);

        var coordinator = new AutopilotRunCoordinator(_Host(), plan);
        await coordinator.AnswerBlockadeAsync("a stray click after the run is done");

        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
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

        (await coordinator.ReportTrackerStageAsync("ceo-pane", "Review")).Should().BeTrue();
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

        (await coordinator.ReportTrackerStageAsync("intruder", "Review")).Should().BeFalse();
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

        (await coordinator.ReportTrackerNoteAsync("ceo-pane", "evidence")).Should().BeFalse();
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
        provider.StageCalls.Should().ContainSingle().Which.Should().Be(("AC-1", "Develop"));

        coordinator.ReportStepDone("step-pane", "opened PR").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
        // Merge-ready moved it to Review (the tracker's own vocabulary via SuggestStageName) — the work is done, the
        // merge is left to the human, so it is not closed to Done automatically.
        provider.StageCalls.Should().Equal(("AC-1", "Develop"), ("AC-1", "Review"));
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
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
        provider.StageCalls.Should().BeEmpty();
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
        provider.StageCalls.Should().ContainSingle().Which.Should().Be(("AC-1", "Develop"));
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
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        // The run completes without faulting despite the tracker throwing on every stage move.
        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
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
        (await coordinator.ReportConsultAsync("step-pane", "Which database should it use?")).Should().BeTrue();
        await _Until(() => ceoSends >= 1);
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

        // Only one consult may be open at a time — a second, while the first is unanswered, is turned down.
        (await coordinator.ReportConsultAsync("step-pane", "and another?")).Should().BeFalse();
        // A pane that is not a live step worker cannot consult.
        (await coordinator.ReportConsultAsync("intruder", "let me in")).Should().BeFalse();

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

        (await coordinator.ReportConsultAsync("step-pane", "Which db?")).Should().BeTrue();
        await _Until(() => ceoSends >= 1);

        // Only the run's CEO session answers a consult — an intruder cannot.
        (await coordinator.AnswerWorkerAsync("intruder", "not you")).Should().BeFalse();

        // The CEO's answer is relayed into the worker's session as a turn; the phase never left Running.
        (await coordinator.AnswerWorkerAsync("ceo-pane", "Use Postgres.")).Should().BeTrue();
        await host.Received(1).SendToSessionAsync("step-pane", "Use Postgres.");
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

        // The consult is cleared: a second answer with none pending is rejected.
        (await coordinator.AnswerWorkerAsync("ceo-pane", "again?")).Should().BeFalse();

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

        (await coordinator.ReportConsultAsync("step-pane", "Need a prod credential.")).Should().BeTrue();
        await _Until(() => ceoSends >= 1);

        // Only the CEO session escalates a consult.
        coordinator.EscalateToOperator("intruder", "nope").Should().BeFalse();

        // The CEO decides it is genuinely the operator's call: the run parks on the operator, and the pending pane is the
        // WORKER (not the CEO), so the operator's answer is later relayed to the worker through the unchanged AnswerBlockadeAsync.
        coordinator.EscalateToOperator("ceo-pane", "The step needs a production credential.").Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        plan.PendingQuestion.Should().Be("The step needs a production credential.");

        await coordinator.AnswerBlockadeAsync("Here is the credential: XYZ.");
        await host.Received(1).SendToSessionAsync("step-pane", "Here is the credential: XYZ.");
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

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
        (await coordinator.ReportConsultAsync("step-pane", "Which db?")).Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        plan.PendingQuestion.Should().Be("Which db?");
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
        (await coordinator.ReportConsultAsync("step-pane", "q1")).Should().BeTrue();
        await _Until(() => ceoSends >= 1);
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);
        (await coordinator.AnswerWorkerAsync("ceo-pane", "a1")).Should().BeTrue();

        // Step 1: the second consult exceeds the cap → it goes to the operator, not the CEO (ceoSends stays 1).
        (await coordinator.ReportConsultAsync("step-pane", "q2")).Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        plan.PendingQuestion.Should().Be("q2");
        ceoSends.Should().Be(1);

        // The operator answers (relayed to the worker), which then finishes step 1 and the CEO validates it.
        await coordinator.AnswerBlockadeAsync("operator says X");
        coordinator.ReportStepDone("step-pane", "done 1").Should().BeTrue();
        await _Until(() => ceoSends >= 2); // the validation turn was sent to the CEO
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        // Step 2 starts with a fresh consult budget: its first consult reaches the CEO again (proving the per-step reset —
        // without it the count would still be over the cap and this would go to the operator).
        await _Until(() => embeds >= 2);
        (await coordinator.ReportConsultAsync("step-pane", "q3")).Should().BeTrue();
        await _Until(() => ceoSends >= 3);
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

        // Finish step 2 cleanly so the run settles.
        (await coordinator.AnswerWorkerAsync("ceo-pane", "a3")).Should().BeTrue();
        coordinator.ReportStepDone("step-pane", "done 2").Should().BeTrue();
        await _Until(() => ceoSends >= 4);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
    }

    private static AutopilotPlanController _RunningPlan(AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [step]));
        plan.BindSession("ceo-pane");
        plan.Approve().Should().BeTrue();
        return plan;
    }

    private static AutopilotPlanController _RunningPlanSteps(params AutopilotStep[] steps)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, steps));
        plan.BindSession("ceo-pane");
        plan.Approve().Should().BeTrue();
        return plan;
    }

    private static AutopilotPlanController _RunningPlanWithSource(AutopilotPlanSource source, AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", source, [step]));
        plan.BindSession("ceo-pane");
        plan.Approve().Should().BeTrue();
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

        condition().Should().BeTrue("the condition should hold within the timeout");
    }

    private static AutopilotRunEnvironment _Env(bool isolate = true) => new("/repo", null, isolate);

    private static Task _DirectUi(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
