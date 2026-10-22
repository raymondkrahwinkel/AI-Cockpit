using System.Text.Json;
using NSubstitute;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Schedules.Tests;

// AC-1545, layer 2, the AC-38 boundary on cockpit-schedules: what an agent creates or changes so that it may write
// files is off until the operator allows it, and a run a task started may only read and create reminders.
public class ScheduleMcpToolsTests
{
    // Criteria 2 and 3: a Write task comes in off with "Needs your approval", and so does one an update makes Write;
    // set_enabled puts it to the operator, Denied keeps it off and Allow switches it on. A ReadOnly task is on at once.
    [Theory]
    [InlineData("create", "read_only", false, true, false, true, 0)]
    [InlineData("create", "write", false, false, true, false, 1)]
    [InlineData("update", "write", true, false, true, true, 1)]
    public async Task AnAgentsWriteTask_IsOffUntilTheOperatorAllowsIt(
        string how, string permission, bool operatorAllows, bool createdEnabled, bool needsApproval, bool finalEnabled, int asks)
    {
        var (tools, host, store, _) = _Tools("pane-1");
        host.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(operatorAllows ? new ConsentDecision(ConsentOutcome.Approved) : ConsentDecision.Denied);

        var created = _Read(_Create(tools, how == "create" ? permission : "read_only"));
        var id = created.GetProperty("id").GetString() ?? throw new InvalidOperationException("No id came back.");
        var result = how == "update" ? _Read(tools.UpdateScheduledTask(id, permission: permission)) : created;
        var enabling = await tools.SetEnabledAsync(id, enabled: true);

        Assert.Equal((createdEnabled, needsApproval), (result.GetProperty("enabled").GetBoolean(), result.TryGetProperty("note", out _)));
        Assert.Equal(finalEnabled, Assert.Single(store.Load() ?? []).IsEnabled);
        Assert.Equal(!finalEnabled, enabling.Contains("Denied", StringComparison.Ordinal));
        Assert.Equal(asks, host.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ICockpitHost.RequestConsentAsync)));
    }

    // Criterion 4: the run a task started (its delegated task id is its verified pane id) cannot create a task, and
    // can create a reminder; any other session creates one as ever.
    [Fact]
    public async Task ARunATaskStarted_MayNotCreateATask_ButMayCreateAReminder()
    {
        var (tools, host, store, agent) = _Tools("task-1");
        host.Actions.DelegateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<string?>(), Arg.Any<Action<string>>())
            .Returns(call =>
            {
                call.Arg<Action<string>>()("task-1");
                return Task.FromResult("OK");
            });
        await agent.RunAsync(EngineRig.NewTask("0 9 * * *") with { Action = new TaskAction(TaskActionKind.AgentPrompt, "Read the news.") { Profile = "Local" } }, CancellationToken.None);

        var refused = _Create(tools, "read_only");
        var reminder = tools.CreateReminder("Call the dentist", "2026-10-23T08:00", "Europe/Amsterdam");
        host.CurrentMcpCallerPaneId.Returns("pane-1");
        var allowed = _Read(_Create(tools, "read_only"));

        Assert.Contains(ScheduleMcpTools.TaskRunsMayNot, refused, StringComparison.Ordinal);
        Assert.DoesNotContain(ScheduleMcpTools.TaskRunsMayNot, reminder, StringComparison.Ordinal);
        Assert.True(allowed.GetProperty("ok").GetBoolean());
        Assert.Single(store.Load() ?? []);
    }

    // AC-38, fail-closed: while a run's delegation has started and its task id is not known yet, no caller may change a
    // task, since none can be told apart from that run; a run that fails before it reports its id opens the tools again.
    [Fact]
    public async Task WhileARunIsStarting_EveryCallerIsRefused_AndARunThatFailsFirstOpensTheToolsAgain()
    {
        var (tools, host, store, agent) = _Tools("pane-1");
        var delegation = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Actions.DelegateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<TimeSpan?>(), Arg.Any<string?>(), Arg.Any<Action<string>>())
            .Returns(_ => delegation.Task);
        var run = agent.RunAsync(EngineRig.NewTask("0 9 * * *") with { Action = new TaskAction(TaskActionKind.AgentPrompt, "Read the news.") { Profile = "Local" } }, CancellationToken.None);

        var whileStarting = _Create(tools, "read_only");
        delegation.SetException(new InvalidOperationException("The profile refused the work."));
        await run;
        var afterwards = _Read(_Create(tools, "read_only"));

        Assert.Contains(ScheduleMcpTools.RunStarting, whileStarting, StringComparison.Ordinal);
        Assert.True(afterwards.GetProperty("ok").GetBoolean());
        Assert.Single(store.Load() ?? []);
    }

    private static string _Create(ScheduleMcpTools tools, string permission) =>
        tools.CreateScheduledTask("Morning news", "30 7 * * 1-5", "Europe/Amsterdam", "agent_prompt", "Summarise the news.", "Local", permission: permission);

    private static JsonElement _Read(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (ScheduleMcpTools Tools, ICockpitHost Host, TaskStore Store, AgentPromptAction Agent) _Tools(string callerPaneId)
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns(callerPaneId);
        var rig = new EngineRig(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
        var delivery = new TaskDelivery(host);
        var agent = new AgentPromptAction(host, delivery, TimeSpan.FromMinutes(1));
        var store = new TaskStore(rig.Storage);
        return (new ScheduleMcpTools(host, store, new RunStore(rig.Storage, rig.Clock), rig.Start(), agent, delivery), host, store, agent);
    }
}
