using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Agents;

/// <summary>
/// AC-1374: a wake decision reads one snapshot (<see cref="ISessionHandle.ReadWakeStateAsync"/>), not the individual
/// properties, which are wired to the wrong answer here on purpose. AC-2005: a wake typed into a TTY reaches only a
/// session an agent started or that opted in itself, never while its operator types, and as one short line.
/// </summary>
public class WorkspaceAgentGatewayWakeStateTests
{
    // Layer 2, contract boundary: the gateway's wake decision for a TTY pane, by origin × operator typing.
    [Theory]
    [InlineData("operator", false, AgentWakeOutcome.NotOptedIn)]
    [InlineData("agent", false, AgentWakeOutcome.Woken)]
    [InlineData("opt-in", true, AgentWakeOutcome.Busy)]
    public async Task TryWakeAsync_IntoATty_WakesOnlyAnAgentsOrOptedInSession_NeverWhileTheOperatorTypes(
        string origin, bool operatorIsTyping, AgentWakeOutcome expected)
    {
        var sessions = new SessionRegistry();
        var caller = _Handle("caller");
        var target = _Handle("target");
        target.DeliversInboxAtTurnStart.Returns(false);
        target.SendPromptAsync(Arg.Any<string>()).Returns(true);
        // Individually wrong on purpose: read one at a time, this pane looks busy and unwakeable.
        target.HasPendingConsent.Returns(true);
        target.SessionStatus.Returns(SessionStatus.Busy);
        target.CanTakeAPrompt.Returns(false);
        target.ReadWakeStateAsync().Returns(new SessionWakeState(
            HasPendingConsent: false, SessionStatus.Idle, CanTakeAPrompt: true, TypesIntoATerminal: true, operatorIsTyping));
        sessions.Register(caller);
        sessions.Register(target);
        var coordinator = new WorkspaceAgentCoordinator();
        if (origin == "agent")
        {
            coordinator.MarkStartedByAnAgent(target.PaneId);
        }
        else if (origin == "opt-in")
        {
            coordinator.SetWakeConsent(target.PaneId, true);
        }

        var gateway = new WorkspaceAgentGateway(sessions, NullLogger<WorkspaceAgentGateway>.Instance, coordinator);

        var outcome = await gateway.TryWakeAsync(caller.PaneId, target.PaneId, "branch");

        Assert.Equal(expected, outcome);
        if (expected == AgentWakeOutcome.Woken)
        {
            await target.Received(1).SendPromptAsync("[cockpit] New message from caller (branch) — call read_inbox");
        }
        else
        {
            await target.DidNotReceive().SendPromptAsync(Arg.Any<string>());
        }
    }

    private static ISessionHandle _Handle(string paneId)
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns(paneId);
        handle.IsTerminal.Returns(false);
        handle.PlacedWorkspaceId.Returns("desk");
        return handle;
    }
}
