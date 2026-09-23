using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Backend.Tests.Agents;

/// <summary>
/// AC-1374: a wake decision reads HasPendingConsent, SessionStatus and CanTakeAPrompt as one snapshot
/// (<see cref="ISessionHandle.ReadWakeStateAsync"/>), not as three separate moments a consent banner or a turn
/// starting could fall between. The individual properties are wired to the wrong answer here on purpose, so the
/// gateway going back to reading them instead of the snapshot turns this red.
/// </summary>
public class WorkspaceAgentGatewayWakeStateTests
{
    [Fact]
    public async Task TryWakeAsync_DecidesFromTheSnapshot_NotFromTheIndividualProperties()
    {
        var sessions = new SessionRegistry();
        var caller = _Handle("caller");
        var target = Substitute.For<ISessionHandle>();
        target.PaneId.Returns("target");
        target.IsTerminal.Returns(false);
        target.PlacedWorkspaceId.Returns("desk");
        target.DeliversInboxAtTurnStart.Returns(false);
        target.SendPromptAsync(Arg.Any<string>()).Returns(true);
        // Individually wrong on purpose: read one at a time, this pane looks busy and unwakeable.
        target.HasPendingConsent.Returns(true);
        target.SessionStatus.Returns(SessionStatus.Busy);
        target.CanTakeAPrompt.Returns(false);
        // The snapshot says the truth: idle, no consent open, ready for a prompt.
        target.ReadWakeStateAsync().Returns(new SessionWakeState(HasPendingConsent: false, SessionStatus.Idle, CanTakeAPrompt: true));
        sessions.Register(caller);
        sessions.Register(target);
        var gateway = new WorkspaceAgentGateway(sessions, NullLogger<WorkspaceAgentGateway>.Instance);

        var outcome = await gateway.TryWakeAsync(caller.PaneId, target.PaneId, "branch");

        Assert.Equal(AgentWakeOutcome.Woken, outcome);
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
