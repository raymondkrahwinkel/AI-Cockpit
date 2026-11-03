using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1543: the agent prompt, against a fake delegation and a fake consent card. Layer 2, a safety boundary (AC-38).
public class AgentPromptActionTests
{
    [Fact]
    public async Task AnAssistantRun_WaitsForItsTurnAndAnswer_WithoutDelegationOrSeparateDelivery()
    {
        var host = Substitute.For<ICockpitHost>();
        var gateway = Substitute.For<IAssistantChannelGateway>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OpenAssistantChannel(Arg.Any<AssistantChannelContribution>()).Returns(gateway);
        gateway.TryStartReadOnlyTurnAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantChannelTurn(AssistantTurnAdmission.Accepted, started.Task, completion.Task)));
        var task = _Task(AgentPermission.Write, "$SCHEDULES_UNSET_DIRECTORY") with
        {
            Action = _Task(AgentPermission.Write, "$SCHEDULES_UNSET_DIRECTORY").Action with { Profile = null },
            Delivery = [new DeliveryTarget("missing-channel")],
        };
        var action = new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMinutes(1));

        Assert.Null(await action.AdmitAsync(task, RunTrigger.Schedule, CancellationToken.None));
        var running = action.RunAsync(task, CancellationToken.None);
        Assert.False(running.IsCompleted);
        started.SetResult();
        Assert.False(running.IsCompleted);
        completion.SetResult("The final answer.");
        var result = await running;
        gateway.TryStartReadOnlyTurnAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new AssistantChannelTurn(AssistantTurnAdmission.Accepted, Task.CompletedTask, Task.FromResult(string.Empty))));
        var empty = await action.RunAsync(task, CancellationToken.None);

        Assert.Equal((RunOutcome.Succeeded, "The final answer.", (string?)null), (result.Outcome, result.Output, result.DelegatedTaskId));
        Assert.Equal((RunOutcome.Succeeded, (string?)null), (empty.Outcome, empty.Output));
        await gateway.Received(2).TryStartReadOnlyTurnAsync("schedules", $"[Schedules: {task.Name}]\nSummarise the notes.", Arg.Any<CancellationToken>());
        Assert.Equal(2, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.OpenAssistantChannel)));
        Assert.DoesNotContain(host.ReceivedCalls(), call => call.GetMethodInfo().Name is nameof(ICockpitHost.ShowToast) or nameof(ICockpitHost.NotifyAssistantAsync) or nameof(ICockpitHost.SendOperatorMessageAsync) or nameof(ICockpitHost.RequestConsentAsync));
        await host.Actions.DidNotReceiveWithAnyArgs().DelegateAsync(string.Empty, string.Empty, null, null, null, Arg.Any<Action<string>>());
    }

    [Fact]
    public async Task AnUnavailableAssistant_SkipsWithoutFallback_WhenAbsentOrItsAdmissionWaitExpires()
    {
        var host = Substitute.For<ICockpitHost>();
        host.OpenAssistantChannel(Arg.Any<AssistantChannelContribution>()).Returns((IAssistantChannelGateway?)null);
        var task = _Task(AgentPermission.ReadOnly, string.Empty) with
        {
            Action = new TaskAction(TaskActionKind.AgentPrompt, "Summarise the notes."),
        };

        var absent = await new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMinutes(1)).RunAsync(task, CancellationToken.None);
        var busy = await _ExpiredAssistantAsync(host, task, AssistantTurnAdmission.Busy);
        var unavailable = await _ExpiredAssistantAsync(host, task, AssistantTurnAdmission.NoAssistant);

        Assert.Equal((RunOutcome.Skipped, "no assistant"), (absent.Outcome, absent.Reason));
        Assert.Equal((RunOutcome.Skipped, "assistant busy"), (busy.Outcome, busy.Reason));
        Assert.Equal((RunOutcome.Skipped, "no assistant"), (unavailable.Outcome, unavailable.Reason));
        await host.Actions.DidNotReceiveWithAnyArgs().DelegateAsync(string.Empty, string.Empty, null, null, null, Arg.Any<Action<string>>());
    }

    private static async Task<ActionResult> _ExpiredAssistantAsync(ICockpitHost host, ScheduledTask task, AssistantTurnAdmission admission)
    {
        var gateway = Substitute.For<IAssistantChannelGateway>();
        host.OpenAssistantChannel(Arg.Any<AssistantChannelContribution>()).Returns(gateway);
        gateway.TryStartReadOnlyTurnAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssistantChannelTurn.Refused(admission)));
        var clock = new TestClock(DateTimeOffset.UnixEpoch);
        var running = new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMinutes(1), clock).RunAsync(task, CancellationToken.None);
        Assert.False(running.IsCompleted);
        clock.Advance(AgentPromptAction.AssistantWait);
        return await running;
    }

    public enum Answer
    {
        None,
        Approved,
        Denied,
        Bypassed,
        NoAnswer,
    }

    // Criteria 1 and 2: Write from the schedule asks unless unattended, the operator's "Run now" never asks, MCP always
    // asks, and a card skipped by the session's "allow all" (Bypassed) counts as no. That a run refused here never runs
    // its action is the engine's part, in ScheduleEngineTests.
    [Theory]
    [InlineData(RunTrigger.Schedule, false, Answer.Approved, true, null)]
    [InlineData(RunTrigger.Schedule, false, Answer.Denied, true, AgentPromptAction.NotApproved)]
    [InlineData(RunTrigger.Schedule, false, Answer.NoAnswer, true, AgentPromptAction.NoAnswer)]
    [InlineData(RunTrigger.Schedule, true, Answer.None, false, null)]
    [InlineData(RunTrigger.Manual, false, Answer.None, false, null)]
    [InlineData(RunTrigger.Mcp, true, Answer.Bypassed, true, AgentPromptAction.NotApproved)]
    public async Task AWriteRun_AsksForConsentOnlyWhenNobodyStartedItByHand(RunTrigger trigger, bool unattended, Answer answer, bool asked, string? refusal)
    {
        var host = _Host(answer);
        var task = _Task(AgentPermission.Write, "/work") with { RunUnattended = unattended };

        var result = await new AgentPromptAction(host, new TaskDelivery(host), _ConsentWait(answer)).AdmitAsync(task, trigger, CancellationToken.None);

        Assert.Equal(refusal, result);
        Assert.Equal(asked ? 1 : 0, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.RequestConsentAsync)));
    }

    // Only the unanswered card may run into the wait: a short one on a card the fake answers at once would let a slow
    // runner's deadline beat that answer, and read a "not approved" as "no answer" (AC-1648).
    private static TimeSpan _ConsentWait(Answer answer) => answer == Answer.NoAnswer ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(1);

    private static ScheduledTask _Task(AgentPermission permission, string directory) => EngineRig.NewTask("0 9 * * *") with
    {
        Action = new TaskAction(TaskActionKind.AgentPrompt, "Summarise the notes.") { Profile = "Local", WorkingDirectory = directory, Permission = permission },
    };

    private static ICockpitHost _Host(Answer answer)
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(async call => answer switch
        {
            Answer.Approved => new ConsentDecision(ConsentOutcome.Approved),
            Answer.Bypassed => new ConsentDecision(ConsentOutcome.Approved, Bypassed: true),
            Answer.NoAnswer => await _NeverAnswered(call.Arg<CancellationToken>()),
            _ => ConsentDecision.Denied,
        });
        return host;
    }

    private static async Task<ConsentDecision> _NeverAnswered(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        return ConsentDecision.Denied;
    }
}
