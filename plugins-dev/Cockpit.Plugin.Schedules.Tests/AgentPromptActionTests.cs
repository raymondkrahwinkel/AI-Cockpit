using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1543: the agent prompt, against a fake delegation and a fake consent card. Layer 2, a safety boundary (AC-38).
public class AgentPromptActionTests
{
    public enum Answer
    {
        None,
        Denied,
        Bypassed,
        NoAnswer,
    }

    // Criteria 1 and 2: Write from the schedule asks unless unattended, the operator's "Run now" never asks, MCP always
    // asks, and a card skipped by the session's "allow all" (Bypassed) counts as no.
    [Theory]
    [InlineData(RunTrigger.Schedule, false, Answer.Denied, true, RunOutcome.Skipped, AgentPromptAction.NotApproved, 0)]
    [InlineData(RunTrigger.Schedule, false, Answer.NoAnswer, true, RunOutcome.Skipped, AgentPromptAction.NoAnswer, 0)]
    [InlineData(RunTrigger.Schedule, true, Answer.None, false, RunOutcome.Succeeded, null, 1)]
    [InlineData(RunTrigger.Manual, false, Answer.None, false, RunOutcome.Succeeded, null, 1)]
    [InlineData(RunTrigger.Mcp, true, Answer.Bypassed, true, RunOutcome.Skipped, AgentPromptAction.NotApproved, 0)]
    public async Task AWriteRun_AsksForConsentOnlyWhenNobodyStartedItByHand(
        RunTrigger trigger, bool unattended, Answer answer, bool asked, RunOutcome outcome, string? reason, int delegations)
    {
        var host = _Host(answer, "OK");
        var task = _Task(AgentPermission.Write) with { RunUnattended = unattended };

        var result = await new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMilliseconds(50)).RunAsync(task, trigger, CancellationToken.None);

        Assert.Equal((outcome, reason), (result.Outcome, result.Reason));
        Assert.Equal(asked ? 1 : 0, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.RequestConsentAsync)));
        Assert.Equal(delegations, host.Actions.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitActions.DelegateAsync)));
    }

    // Criteria 3 and 4: an empty answer is nothing to deliver and an answer is delivered; a working directory the
    // profile does not allow fails with the host's reason, refused before a task exists. $VAR is expanded first.
    [Theory]
    [InlineData(" \n ", null, RunOutcome.NothingToDeliver, "task-1", 0)]
    [InlineData("OK", null, RunOutcome.Succeeded, "task-1", 1)]
    [InlineData(null, "Profile 'Local' does not allow a task to run in '/work/notes'.", RunOutcome.Failed, null, 0)]
    public async Task TheAnswer_IsDeliveredUnlessEmpty_AndARefusedDirectoryFails(string? answer, string? refusal, RunOutcome outcome, string? taskId, int toasts)
    {
        Environment.SetEnvironmentVariable("SCHEDULES_TEST_ROOT", "/work");
        var host = _Host(Answer.None, answer, refusal);
        var task = _Task(AgentPermission.ReadOnly) with { Action = _Task(AgentPermission.ReadOnly).Action with { WorkingDirectory = "$SCHEDULES_TEST_ROOT/notes" } };

        var result = await new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMinutes(1)).RunAsync(task, RunTrigger.Schedule, CancellationToken.None);

        Assert.Equal((outcome, refusal, taskId), (result.Outcome, result.Reason, result.DelegatedTaskId));
        Assert.Equal(toasts, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.ShowToast)));
        await host.Actions.Received(1).DelegateAsync("Local", "Summarise the notes.", "/work/notes", null, null, Arg.Any<Action<string>>());
    }

    private static ScheduledTask _Task(AgentPermission permission) => EngineRig.NewTask("0 9 * * *") with
    {
        Action = new TaskAction(TaskActionKind.AgentPrompt, "Summarise the notes.") { Profile = "Local", Permission = permission },
    };

    // As the host's delegation: a refusal throws before a task exists, otherwise the task id comes before the answer.
    private static ICockpitHost _Host(Answer answer, string? reply, string? refusal = null)
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(async call => answer switch
        {
            Answer.Bypassed => new ConsentDecision(ConsentOutcome.Approved, Bypassed: true),
            Answer.NoAnswer => await _NeverAnswered(call.Arg<CancellationToken>()),
            _ => ConsentDecision.Denied,
        });
        host.Actions.DelegateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<string?>(), Arg.Any<Action<string>>())
            .Returns(call =>
            {
                if (refusal is not null)
                {
                    return Task.FromException<string>(new InvalidOperationException(refusal));
                }

                call.Arg<Action<string>>()("task-1");
                return Task.FromResult(reply ?? string.Empty);
            });
        return host;
    }

    private static async Task<ConsentDecision> _NeverAnswered(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
        return ConsentDecision.Denied;
    }
}
