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
    public void ReportBlocked_WhenTheRunIsNotRunning_IsRejected()
    {
        var plan = new AutopilotPlanController();
        plan.BindSession("ceo-pane");
        var coordinator = new AutopilotRunCoordinator(Substitute.For<ICockpitHost>(), plan);

        coordinator.ReportBlocked("ceo-pane", "which db?").Should().BeFalse();
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

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);

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

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        coordinator.ReportStepDone("step-pane", "tried but it does not compile").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: false, reason: "does not meet acceptance").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.Blocked);
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
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _DirectUi, cts.Token);

        await shown.Task.WaitAsync(Timeout);
        context.Received().EmbedSession(Arg.Is<EmbeddedSessionRequest>(request => request.StartWithInputDisabled && request.IsolateInWorktree));

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
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);

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
        var ended = new TaskCompletionSource();
        var context = _Context(_Session("step-pane", ended.Task));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(maxAttempts: 1), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);

        await shown.Task.WaitAsync(Timeout);
        // The step session ends without ever reporting done — the fail-closed refusal in the host.
        ended.TrySetResult();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.Blocked);
        // The step never reported done, so the CEO is never asked to validate it.
        await host.DidNotReceive().SendToSessionAsync("ceo-pane", Arg.Any<string>());
    }

    [Fact]
    public async Task ReportBlocked_DuringAStep_ParksTheRun_ThenAnswerResumesTheSameSession()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);

        // The step agent raises a blockade — the run parks and the operator's answer is relayed to that same session.
        coordinator.ReportBlocked("step-pane", "Which database should it use?").Should().BeTrue();
        plan.Phase.Should().Be(AutopilotPlanPhase.AwaitingOperator);
        plan.PendingQuestion.Should().Be("Which database should it use?");

        await coordinator.AnswerBlockadeAsync("Use Postgres.");
        await host.Received(1).SendToSessionAsync("step-pane", "Use Postgres.");
        plan.Phase.Should().Be(AutopilotPlanPhase.Running);

        // The agent carries on and finishes as usual.
        coordinator.ReportStepDone("step-pane", "used Postgres, opened PR").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();

        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
    }

    [Fact]
    public async Task ReportValidation_AfterABlockadeLeftRunning_IsRejected_UntilTheRunResumes()
    {
        var plan = _RunningPlan(_HardStep("1"));
        var host = _Host();
        var context = _Context(_Session("step-pane"));
        var coordinator = new AutopilotRunCoordinator(host, plan);

        var shown = new TaskCompletionSource();
        var validationSent = new TaskCompletionSource();
        host.When(h => h.SendToSessionAsync("ceo-pane", Arg.Any<string>())).Do(_ => validationSent.TrySetResult());

        var run = coordinator.RunAsync(context, _Session("ceo-pane"), _Settings(), _ => shown.TrySetResult(), _DirectUi, CancellationToken.None);
        await shown.Task.WaitAsync(Timeout);
        coordinator.ReportStepDone("step-pane", "done").Should().BeTrue();
        await validationSent.Task.WaitAsync(Timeout);

        // The CEO both blocks and validates in one turn: the block moves the run off Running, so the validate must not
        // resolve mid-blockade and corrupt the run.
        coordinator.ReportBlocked("ceo-pane", "one more question").Should().BeTrue();
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeFalse();

        // Once answered and running again, the pending validation resolves as normal and the run settles.
        await coordinator.AnswerBlockadeAsync("go ahead");
        coordinator.ReportValidation("ceo-pane", passed: true, reason: "ok").Should().BeTrue();
        await run.WaitAsync(Timeout);
        plan.Phase.Should().Be(AutopilotPlanPhase.MergeReady);
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

    private static AutopilotPlanController _RunningPlan(AutopilotStep step)
    {
        var plan = new AutopilotPlanController();
        plan.BeginPlanning(new AutopilotPlan("goal", null, [step]));
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

    private static IEmbeddedSession _Session(string paneId, Task? completion = null)
    {
        var session = Substitute.For<IEmbeddedSession>();
        session.View.Returns(new TextBlock());
        session.PaneId.Returns(paneId);
        session.CloseAsync().Returns(Task.CompletedTask);
        // A live session's Completion has not fired; a never-completing task models that, so the coordinator waits on
        // the step's done-report as usual. A test that wants to model a session ending early passes its own task.
        session.Completion.Returns(completion ?? new TaskCompletionSource().Task);
        return session;
    }

    private static AutopilotSettings _Settings(int? maxAttempts = null)
    {
        var storage = Substitute.For<IPluginStorage>();
        if (maxAttempts is { } cap)
        {
            storage.Get<int?>("maxSelfFixAttempts").Returns(cap);
        }

        return new AutopilotSettings(storage);
    }

    private static Task _DirectUi(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
