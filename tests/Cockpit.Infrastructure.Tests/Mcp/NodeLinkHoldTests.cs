using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1943: a node that flaps costs the assistant one first-drop wake, one "unstable" and one "back" instead of a pair
/// per flap; a held (disconnected or paused) node costs it nothing; and a pause outlives a restart.
/// </summary>
public sealed class NodeLinkHoldTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task FlappingNode_IsBatched_HeldNodeIsSilent_AndPauseSurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"paused-nodes-{Guid.NewGuid():N}.txt");
        try
        {
            var clock = new Clock();
            var t0 = clock.Now;
            var inbox = new AgentMessageInbox();
            var gate = new NodeLinkGate(path);
            var nodes = Substitute.For<INodeSessionsClient>();
            var running = new NodeSessionRow("pane-1", "work", "profile", "");
            nodes.TryGetLastSnapshot("jsll").Returns<(NodeSessionsSnapshot Snapshot, DateTimeOffset AtUtc)?>((new NodeSessionsSnapshot("jsll", [running], [], []), t0));
            nodes.ReadInboxAsync("jsll", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new NodeInboxBatch("jsll", []));
            var relay = new NodeInboxRelay(nodes, inbox, gate, timeProvider: clock);

            // Three drops inside ten minutes, each with a return: the first two are announced, the third turns "unstable".
            for (var minute = 0; minute < 6; minute += 2)
            {
                relay.NotifyTransition("jsll", false, 0, t0.AddMinutes(minute));
                relay.NotifyTransition("jsll", true, 1, t0.AddMinutes(minute + 1));
            }

            // Held back from here on: the next flap, and the poll before the quiet period has passed.
            relay.NotifyTransition("jsll", false, 0, t0.AddMinutes(6));
            relay.NotifyTransition("jsll", true, 1, t0.AddMinutes(7));
            clock.Now = t0.AddMinutes(10);
            await relay.PollAsync("jsll");
            clock.Now = t0.AddMinutes(13);
            await relay.PollAsync("jsll");
            await relay.PollAsync("jsll");

            // A disconnected node: no mail about it, and nothing read from it.
            gate.SetDisconnected("jsll", true);
            relay.NotifyTransition("jsll", false, 0, t0.AddMinutes(20));
            await relay.PollAsync("jsll");

            Assert.Equal(
                ["node-dropped", "node-back", "node-dropped", "node-back", "node-unstable", "node-back"],
                inbox.Drain(AssistantIdentity.PaneId, 25).Messages.Select(message => message.Kind));
            await nodes.Received(3).ReadInboxAsync("jsll", Arg.Any<string?>(), Arg.Any<CancellationToken>());

            gate.SetPaused("jsll", true);
            Assert.True(new NodeLinkGate(path).IsPaused("jsll"));
            Assert.Null(new NodeLinkGate(path).RefusalFor("other"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
