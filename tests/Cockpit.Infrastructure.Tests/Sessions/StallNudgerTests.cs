using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1706 (L2c, the turn-end protocol): a turn closed by a notify gets nothing; a turn that only set its status gets
// one nudge in its own inbox; stalling again goes to the assistant as `stalled`, never as a second nudge.
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

        _Turn(handle, "mcp__cockpit-agents__notify");
        Assert.Empty(inbox.ReceivedCalls());

        _Turn(handle, "mcp__cockpit-session__set_status");
        inbox.Received(1).Deliver(Arg.Any<string>(), "builder", StallNudger.NudgeKind, StallNudger.Nudge);

        _Turn(handle, "set_status");
        inbox.Received(1).Deliver(Arg.Any<string>(), "builder", StallNudger.NudgeKind, StallNudger.Nudge);
        inbox.Received(1).Deliver(
            Arg.Any<string>(), AssistantIdentity.PaneId, "session", Arg.Is<string>(body => body.Contains("stalled")),
            subjectPaneId: "builder");
    }

    private static void _Turn(ISessionHandle handle, string toolName)
    {
        handle.ToolActivityProduced += Raise.Event<Action<SessionToolCall>>(new SessionToolCall("builder", toolName, "{}", "ok", false));
        handle.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(Completed);
    }
}
