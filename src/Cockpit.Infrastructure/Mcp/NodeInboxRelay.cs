using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Assistant;

namespace Cockpit.Infrastructure.Mcp;

// AC-1322: the controller's end of a node's `notify cockpit-assistant` — collects that node's queue on the 20s poll
// that reads its sessions, via the ordinary `Deliver` path so `InboxWakeScheduler` wakes the assistant as for local
// mail. A singleton so the per-node cursor outlives the node card, which the Security tab rebuilds.
public sealed class NodeInboxRelay(
    INodeSessionsClient nodes,
    IAgentMessageInbox inbox,
    INodeLinkGate gate,
    ILogger<NodeInboxRelay>? logger = null,
    TimeProvider? timeProvider = null) : INodeInboxRelay, ISingletonService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private readonly ILogger<NodeInboxRelay> _logger = logger ?? NullLogger<NodeInboxRelay>.Instance;

    // The last id delivered here per node, sent back as `afterMessageId` so the node drops it. Moves only once a
    // message is in the local inbox, so a failed read or a full inbox leaves the node holding it for the next poll.
    // ponytail: in-memory — a controller restart re-collects at most the last batch (doubled, never lost).
    private readonly ConcurrentDictionary<string, string> _lastDelivered = new(StringComparer.Ordinal);

    public async Task PollAsync(string nodeName, CancellationToken cancellationToken = default)
    {
        if (gate.RefusalFor(nodeName) is not null)
        {
            return;
        }

        _SettleIfQuiet(nodeName);
        _lastDelivered.TryGetValue(nodeName, out var after);
        var batch = await nodes.ReadInboxAsync(nodeName, after, cancellationToken).ConfigureAwait(false);
        if (batch.Error is { Length: > 0 } error)
        {
            _logger.LogDebug("The assistant's mail on node {Node} was not read: {Error}", nodeName, error);
            return;
        }

        foreach (var message in batch.Messages)
        {
            var from = NodeSessionAddress.For(nodeName, message.FromPaneId);
            var delivery = inbox.Deliver(from, AssistantIdentity.PaneId, message.Kind, Origin(nodeName, batch.DiscoveryId, from) + message.Body);
            if (delivery.Message is null)
            {
                // Full: leave this and everything after it on the node — the cursor stays where it is, and the
                // next poll asks for the same messages once the assistant has read some mail.
                _logger.LogWarning("The assistant's inbox is full; mail from node {Node} stays there until it is read.", nodeName);
                return;
            }

            _lastDelivered[nodeName] = message.Id;
        }
    }

    // The origin line every relayed message opens with: which machine, its stable id, and the address the sender
    // has here — plus the one thing the assistant must not try, since replying to a node is not built yet.
    internal static string Origin(string nodeName, string discoveryId, string from) =>
        $"[From node {nodeName}" + (discoveryId is { Length: > 0 } ? $" ({discoveryId})" : "")
        + $", session {from}. Reply with send_message to that address; notify does not reach it.] ";

    // AC-1943: three drops within ten minutes make a node "unstable"; it counts as stable again after five quiet minutes.
    private static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(5);
    private const int FlapDrops = 3;

    private sealed class Flap
    {
        public readonly List<DateTimeOffset> Drops = [];
        public bool Unstable;
        public bool DropAnnounced;
        public DateTimeOffset LastChange;
        public bool Reachable = true;
    }

    private readonly ConcurrentDictionary<string, Flap> _flaps = new(StringComparer.Ordinal);

    // AC-1327 criterion 2: one message per reachable↔unreachable transition, never per poll — same Deliver path
    // as the mail relay above, so the controller's assistant hears about the node itself the way it hears from
    // the node's own agents.
    // AC-1943: nothing for a held node; and a node that keeps flapping gets one "node-unstable" then one "node-back"
    // once it has stayed up (`_SettleIfQuiet`, on the poll) instead of a pair of mails per flap. The first drop still
    // wakes the assistant when sessions run there, since that is the one it can act on.
    public void NotifyTransition(string nodeName, bool reachable, int sessionCount, DateTimeOffset atUtc)
    {
        if (gate.RefusalFor(nodeName) is not null)
        {
            return;
        }

        var flap = _flaps.GetOrAdd(nodeName, _ => new Flap());
        lock (flap)
        {
            flap.Reachable = reachable;
            flap.LastChange = atUtc;
            if (flap.Unstable)
            {
                return;
            }

            if (reachable)
            {
                if (flap.DropAnnounced)
                {
                    flap.DropAnnounced = false;
                    inbox.Deliver(nodeName, AssistantIdentity.PaneId, "node-back", $"Node {nodeName} is reachable again as of {atUtc:u}, with {sessionCount} session(s) running there.");
                }

                return;
            }

            flap.Drops.RemoveAll(at => atUtc - at > FlapWindow);
            flap.Drops.Add(atUtc);
            if (flap.Drops.Count >= FlapDrops)
            {
                flap.Unstable = true;
                inbox.Deliver(nodeName, AssistantIdentity.PaneId, "node-unstable", $"Node {nodeName} keeps dropping ({flap.Drops.Count} times since {flap.Drops[0]:u}). Further drops and returns are held back; one node-back follows when it has stayed up for {QuietPeriod.TotalMinutes:0} minutes.");
                return;
            }

            if (nodes.TryGetLastSnapshot(nodeName)?.Snapshot.Sessions.Count > 0)
            {
                flap.DropAnnounced = true;
                inbox.Deliver(nodeName, AssistantIdentity.PaneId, "node-dropped", $"Node {nodeName} stopped answering as of {atUtc:u}.");
            }
        }
    }

    // Called by the poll of a reachable node: the one "node-back" for an unstable node, once it has been up for the quiet period.
    private void _SettleIfQuiet(string nodeName)
    {
        if (!_flaps.TryGetValue(nodeName, out var flap))
        {
            return;
        }

        lock (flap)
        {
            var now = _clock.GetUtcNow();
            if (!flap.Unstable || !flap.Reachable || now - flap.LastChange < QuietPeriod)
            {
                return;
            }

            flap.Unstable = false;
            flap.DropAnnounced = false;
            flap.Drops.Clear();
            var sessions = nodes.TryGetLastSnapshot(nodeName)?.Snapshot.Sessions.Count ?? 0;
            inbox.Deliver(nodeName, AssistantIdentity.PaneId, "node-back", $"Node {nodeName} is stable again as of {now:u}, with {sessions} session(s) running there.");
        }
    }
}
