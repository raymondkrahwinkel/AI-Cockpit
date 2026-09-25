using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

// AC-1399 (D6): cockpit.inject and cockpit.set-status act on the session their Session parameter names — a name, a
// pane id, or what an earlier Start session step handed on — and never on "the active one". Every flow here is fired
// by its schedule, as a timer would, while the host says another session is the active one.
public class SessionStepTests
{
    // Acceptance 2: the named session gets the text, placed and not sent, and no other session does.
    [Theory]
    [InlineData("api")]
    [InlineData("API")]
    [InlineData("pane-2")]
    public async Task AScheduledInject_ReachesTheSessionItNames_NotTheActiveOne(string session)
    {
        var host = _Host();

        var run = await _RunScheduledAsync(host, _Step("cockpit.inject", ("Session", session), ("Text", "hello")));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        await host.Received(1).InsertIntoSessionAsync("pane-2", "hello");
        await host.DidNotReceive().InsertIntoSessionAsync(Arg.Is<string>(pane => pane != "pane-2"), Arg.Any<string>());
        await host.DidNotReceive().SendToSessionAsync(Arg.Any<string>(), Arg.Any<string>());
        await host.Actions.DidNotReceive().InjectIntoActiveSessionAsync(Arg.Any<string>());
    }

    // The counter-proof: a step that names no live session fails with the reason, and no session is touched — apart
    // from the headless one, which was asked and took nothing.
    [Theory]
    [InlineData("cockpit.inject", "", "This step names no session")]
    [InlineData("cockpit.set-status", "", "This step names no session")]
    [InlineData("cockpit.inject", "nowhere", "No open session is called 'nowhere'")]
    [InlineData("cockpit.set-status", "twin", "2 open sessions are called 'twin'")]
    [InlineData("cockpit.inject", "headless", "'headless' took no text")]
    [InlineData("cockpit.set-status", "cockpit-assistant", "No open session is called 'cockpit-assistant'")]
    public async Task AStepThatNamesNoLiveSession_FailsWithItsReason_AndTouchesNoSession(string typeId, string session, string reason)
    {
        var host = _Host();

        var run = await _RunScheduledAsync(host, _Step(typeId, ("Session", session), ("Text", "hello"), ("Status", "AC-1399")));

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains(reason, run.Steps[^1].Note);
        await host.DidNotReceive().InsertIntoSessionAsync(Arg.Is<string>(pane => pane != "pane-3"), Arg.Any<string>());
        await host.DidNotReceive().SetSessionStatusline(Arg.Any<string>(), Arg.Any<string>());
        await host.Actions.DidNotReceive().InjectIntoActiveSessionAsync(Arg.Any<string>());
        await host.Actions.DidNotReceive().SetActiveSessionStatusAsync(Arg.Any<string?>(), Arg.Any<string?>());
    }

    // Acceptance 3: a flow that starts a session and then injects into it reaches the session it started.
    [Fact]
    public async Task AnInjectAfterStartSession_ReachesTheSessionThatStepStarted()
    {
        var host = _Host();
        host.Actions.StartSessionAsync("Claude", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns("api");
        var start = _Step("cockpit.start-session", ("Profile", "Claude"));
        start.Name = "Start session";

        var run = await _RunScheduledAsync(host, start, _Step("cockpit.inject", ("Session", "{Start session.session}"), ("Text", "go")));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        await host.Received(1).InsertIntoSessionAsync("pane-2", "go");
    }

    [Fact]
    public async Task SetStatus_LabelsAndRenamesTheSessionItNames()
    {
        var host = _Host();

        var run = await _RunScheduledAsync(host, _Step("cockpit.set-status", ("Session", "api"), ("Status", "AC-1399"), ("Name", "Workflows")));

        Assert.Equal(RunStatus.Succeeded, run.Status);
        await host.Received(1).SetSessionStatusline("pane-2", "AC-1399");
        await host.Received(1).SetSessionName("pane-2", "Workflows");
        await host.Actions.DidNotReceive().SetActiveSessionStatusAsync(Arg.Any<string?>(), Arg.Any<string?>());
    }

    // The open sessions while the host reports an active one: the one the flows name (pane-2), a headless one, two that
    // share a name, and the assistant, which no step may act on. Consent is not asked: the flows run unattended.
    private static ICockpitHost _Host()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Actions.HasActiveSession.Returns(true);
        host.Sessions.OpenSessions.Returns(
        [
            new OpenCockpitSession("pane-1", "webshop"),
            new OpenCockpitSession("pane-2", "api"),
            new OpenCockpitSession("pane-3", "headless"),
            new OpenCockpitSession("pane-4", "twin"),
            new OpenCockpitSession("pane-5", "twin"),
            new OpenCockpitSession("cockpit-assistant", "Assistant"),
        ]);
        host.InsertIntoSessionAsync(Arg.Is<string>(pane => pane != "pane-3"), Arg.Any<string>()).Returns(true);
        return host;
    }

    private static WorkflowNode _Step(string typeId, params (string Name, string Value)[] parameters)
    {
        var node = new WorkflowNode { Id = Guid.NewGuid().ToString("n"), TypeId = typeId, Name = typeId };
        foreach (var (name, value) in parameters)
        {
            node.Parameters[name] = value;
        }

        return node;
    }

    // The steps wired one after the other behind a schedule trigger, run as that trigger fires it.
    private static Task<WorkflowRun> _RunScheduledAsync(ICockpitHost host, params WorkflowNode[] steps)
    {
        var trigger = new WorkflowNode { Id = "t", TypeId = "cockpit.schedule", Name = "Schedule", Parameters = { ["When"] = "every 15m" } };
        var flow = new Workflow { Id = "f", Name = "Scheduled", RunUnattended = true, Nodes = { trigger } };
        var previous = trigger;
        foreach (var step in steps)
        {
            flow.Nodes.Add(step);
            flow.Connections.Add(new WorkflowConnection { FromNodeId = previous.Id, FromOutput = 0, ToNodeId = step.Id });
            previous = step;
        }

        return EngineFactory.Create(host, []).RunAsync(flow, trigger.Id, RunOrigin.Trigger);
    }
}
