using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class StallNudgerTests
{
    // F: the watch must distinguish a finished or waiting session from one that abandoned real work.
    [Fact]
    public void TurnEnd_ReportsOnlyUnclosedWork()
    {
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("execute", true, 0, 0);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("execute", false, 1, 1);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("notify,administration", false, 0, 0);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("start_run,administration", false, 0, 0);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("notify|execute", false, 1, 1);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("notify", false, 0, 0);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("start_run", false, 0, 0);
        }
        {
            var scenario = new StallNudgerTests();
            scenario._AssertTurnEnd_ReportsOnlyUnclosedWork("notify|write", false, 1, 1);
        }
    }

    private void _AssertTurnEnd_ReportsOnlyUnclosedWork(string tools, bool outstandingRun, int nudges, int reports)
    {
        foreach (var prefix in new[] { "", "mcp__cockpit__" })
        {
            var handle = Substitute.For<ISessionHandle>();
            handle.PaneId.Returns("worker");
            handle.StartedByTheAssistant.Returns(true);
            handle.LiveState.Returns(SessionLiveState.None);
            handle.ReadWakeStateAsync().Returns(new SessionWakeState(false, SessionStatus.Idle, true));
            handle.ReadPendingPermissionsAsync().Returns(Array.Empty<SessionPendingPermission>());
            handle.HasOutstandingBackgroundShellsAsync().Returns(outstandingRun);
            var registry = Substitute.For<ISessionRegistry>();
            registry.All.Returns(new[] { handle });
            var inbox = Substitute.For<IAgentMessageInbox>();
            using var nudger = new StallNudger(inbox);
            nudger.Start(registry);

            var turns = tools.Split('|');
            if (turns.Length == 2)
            {
                handle.ToolActivityProduced += Raise.Event<Action<SessionToolCall>>(
                    new SessionToolCall("worker", prefix + turns[0], "{}", "", false));
                handle.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(new SessionTurnEnd(false, false, false, null, null, null));
                Assert.Empty(inbox.ReceivedCalls());
            }

            foreach (var tool in turns[^1].Split(','))
            {
                var names = tool == "administration"
                    ? new[] { "set_status", "run_status", "report_job_progress", "append", "patch_section", "write_many", "release" }.Select(name => prefix + name).ToArray()
                    : new[] { prefix + tool };
                foreach (var name in names)
                {
                    handle.ToolActivityProduced += Raise.Event<Action<SessionToolCall>>(
                        new SessionToolCall("worker", name, "{}", "", false));
                }
            }

            for (var turn = 0; turn < 2; turn++)
            {
                handle.TurnEnded += Raise.Event<Action<SessionTurnEnd>>(new SessionTurnEnd(false, false, false, null, null, null));
                var deliveries = inbox.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IAgentMessageInbox.Deliver)).ToArray();
                Assert.Equal(nudges, deliveries.Count(call => Equals(call.GetArguments()[1], "worker")));
                Assert.Equal(turn == 0 ? 0 : reports, deliveries.Count(call => Equals(call.GetArguments()[1], AssistantIdentity.PaneId)));
            }
        }
    }
}
