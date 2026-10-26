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
        var host = _Host(answer, "OK");
        var task = _Task(AgentPermission.Write, "/work") with { RunUnattended = unattended };

        var result = await new AgentPromptAction(host, new TaskDelivery(host), _ConsentWait(answer)).AdmitAsync(task, trigger, CancellationToken.None);

        Assert.Equal(refusal, result);
        Assert.Equal(asked ? 1 : 0, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.RequestConsentAsync)));
    }

    // Criteria 3 and 4: an empty answer is nothing to deliver and an answer is delivered; a working directory the
    // profile does not allow fails with the host's reason, refused before a task exists. $VAR is expanded first.
    [Theory]
    [InlineData(" \n ", AgentPermission.ReadOnly, null, null, RunOutcome.NothingToDeliver, "task-1", 0)]
    [InlineData("OK", AgentPermission.Write, "acceptEdits", null, RunOutcome.Succeeded, "task-1", 1)]
    [InlineData(null, AgentPermission.ReadOnly, null, "Profile 'Local' does not allow a task to run in '/work/notes'.", RunOutcome.Failed, null, 0)]
    public async Task TheAnswer_IsDeliveredUnlessEmpty_AndARefusedDirectoryFails(
        string? answer, AgentPermission permission, string? sentPermission, string? refusal, RunOutcome outcome, string? taskId, int toasts)
    {
        var host = _Host(Answer.None, answer, refusal);
        Environment.SetEnvironmentVariable("SCHEDULES_TEST_ROOT", "/work");
        try
        {
            var result = await new AgentPromptAction(host, new TaskDelivery(host), TimeSpan.FromMinutes(1)).RunAsync(_Task(permission, "$SCHEDULES_TEST_ROOT/notes"), CancellationToken.None);

            Assert.Equal((outcome, refusal, taskId), (result.Outcome, result.Reason, result.DelegatedTaskId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCHEDULES_TEST_ROOT", null);
        }

        Assert.Equal(toasts, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.ShowToast)));
        await host.Actions.Received(1).DelegateAsync("Local", "Summarise the notes.", "/work/notes", null, sentPermission, Arg.Any<Action<string>>());
    }

    // Only the unanswered card may run into the wait: a short one on a card the fake answers at once would let a slow
    // runner's deadline beat that answer, and read a "not approved" as "no answer" (AC-1648).
    private static TimeSpan _ConsentWait(Answer answer) => answer == Answer.NoAnswer ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMinutes(1);

    private static ScheduledTask _Task(AgentPermission permission, string directory) => EngineRig.NewTask("0 9 * * *") with
    {
        Action = new TaskAction(TaskActionKind.AgentPrompt, "Summarise the notes.") { Profile = "Local", WorkingDirectory = directory, Permission = permission },
    };

    // As the host's delegation: a refusal throws before a task exists, otherwise the task id comes before the answer.
    private static ICockpitHost _Host(Answer answer, string? reply, string? refusal = null)
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>()).Returns(async call => answer switch
        {
            Answer.Approved => new ConsentDecision(ConsentOutcome.Approved),
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
