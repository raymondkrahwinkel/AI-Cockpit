using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1706 (L2c, the turn-end protocol): a turn whose last tool is a notify gets nothing, a later set_status included;
// AC-1681's own turn (notify, set_status, a Depot append) gets one nudge; a set_status-only stall after it goes to the
// assistant as `stalled`, never as a second nudge.
public class StallNudgerTests
{
    private static readonly SessionTurnEnd Completed = new(false, false, false, null, null, null);

    [Fact]
    public void ASessionOfTheAssistant_IsNudgedOnceAfterAStall_AndReportedToTheAssistantOnTheNext()
    {
        var handle = Substitute.For<ISessionHandle>();
        handle.PaneId.Returns("builder");
        handle.Title.Returns("AC-1681");
        handle.StartedByTheAssistant.Returns(true);
        handle.LiveState.Returns(SessionLiveState.None);
        handle.ReadWakeStateAsync().Returns(new SessionWakeState(false, SessionStatus.Idle, true));
        var registry = Substitute.For<ISessionRegistry>();
        registry.All.Returns(new[] { handle });
        var inbox = Substitute.For<IAgentMessageInbox>();
        using var nudger = new StallNudger(inbox);
        nudger.Start(registry);

        _Turn(handle, "mcp__cockpit-agents__notify", "mcp__cockpit-session__set_status");
        Assert.Empty(inbox.ReceivedCalls());

        _Turn(handle, "notify", "set_status", "mcp__Depot__depot_krahwinkel-it_nl__append");
        inbox.Received(1).Deliver(Arg.Any<string>(), "builder", StallNudger.NudgeKind, StallNudger.Nudge);

        _Turn(handle, "set_status");
        inbox.Received(1).Deliver(Arg.Any<string>(), "builder", StallNudger.NudgeKind, StallNudger.Nudge);
        inbox.Received(1).Deliver(
            Arg.Any<string>(), AssistantIdentity.PaneId, "session", Arg.Is<string>(body => body.Contains("stalled")),
            subjectPaneId: "builder");
    }

    private static void _Turn(ISessionHandle handle, params string[] toolNames)
    {
        foreach (var toolName in toolNames)
        {
            handle.ToolActivityProduced += Raise.Event<Action<SessionToolCall>>(new SessionToolCall("builder", toolName, "{}", "ok", false));
        }

        handle.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(Completed);
    }
}
