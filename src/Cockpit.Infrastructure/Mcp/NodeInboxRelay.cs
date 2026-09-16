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
    ILogger<NodeInboxRelay>? logger = null) : ISingletonService
{
    private readonly ILogger<NodeInboxRelay> _logger = logger ?? NullLogger<NodeInboxRelay>.Instance;

    // The last id delivered here per node, sent back as `afterMessageId` so the node drops it. Moves only once a
    // message is in the local inbox, so a failed read or a full inbox leaves the node holding it for the next poll.
    // ponytail: in-memory — a controller restart re-collects at most the last batch (doubled, never lost).
    private readonly ConcurrentDictionary<string, string> _lastDelivered = new(StringComparer.Ordinal);

    public async Task PollAsync(string nodeName, CancellationToken cancellationToken = default)
    {
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
}
