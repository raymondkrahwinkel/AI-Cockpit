using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete inbox store behind <see cref="IAgentMessageInbox"/> (AC-392): one list of waiting messages per
/// recipient pane. Everything is behind one lock rather than a concurrent dictionary — unlike the roster, a
/// delivery is a check-then-act (is an identical message already waiting? is the inbox full?) that has to be
/// atomic, or two notifies landing on different MCP request threads at the same moment both see "no duplicate"
/// and both add one. The state is small and every call is short, so a single lock is the simpler correct answer.
/// </summary>
internal sealed class AgentMessageInbox : IAgentMessageInbox, ISingletonService
{
    /// <summary>
    /// Cap on one pane's waiting messages. A recipient that never calls <c>read_inbox</c> — an agent that has this
    /// server mounted but never looks — would otherwise let a neighbour on the same desk grow host memory without
    /// bound, one distinct body at a time, and make the duplicate scan below linear in that. Past the cap the
    /// sender is told outright rather than the oldest message being dropped: silently discarding mail the sender
    /// was told had arrived is the failure this line exists to avoid.
    /// </summary>
    internal const int MaxWaitingPerPane = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<AgentMessage>> _inboxes = new(StringComparer.Ordinal);

    public AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body)
    {
        lock (_lock)
        {
            _inboxes.TryGetValue(toPaneId, out var waiting);

            // Dedup is on the message's content, not on an id the sender chose: the sender re-sending because it
            // did not see an answer yet is the case this is for, and it has no id from the first attempt to repeat.
            // Only messages still waiting count — one the recipient has already read and acted on is not a
            // duplicate, and a sender must be able to say the same thing again later.
            if (waiting?.FirstOrDefault(message => _IsSame(message, fromPaneId, toPaneId, kind, body)) is { } duplicate)
            {
                return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Deduplicated, duplicate);
            }

            if (waiting is { Count: >= MaxWaitingPerPane })
            {
                return new AgentMessageDelivery(AgentMessageDeliveryOutcome.RecipientInboxFull, null);
            }

            if (waiting is null)
            {
                // Created only once something is actually going in it, so a refused delivery never leaves an empty
                // inbox behind under a pane id.
                waiting = [];
                _inboxes[toPaneId] = waiting;
            }

            var delivered = new AgentMessage(
                Guid.NewGuid().ToString("n"),
                fromPaneId,
                toPaneId,
                kind,
                body,
                DateTimeOffset.UtcNow);
            waiting.Add(delivered);
            return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Delivered, delivered);
        }
    }

    public AgentInboxBatch Drain(string paneId, int limit)
    {
        lock (_lock)
        {
            if (!_inboxes.TryGetValue(paneId, out var waiting))
            {
                return new AgentInboxBatch([], 0);
            }

            if (limit <= 0)
            {
                // Nothing handed over, and the caller is told everything is still waiting — a drain that hands over
                // nothing must not read as an empty inbox.
                return new AgentInboxBatch([], waiting.Count);
            }

            var take = Math.Min(limit, waiting.Count);

            // GetRange copies, so the list handed back is no longer reachable from here: nothing that arrives
            // afterwards can appear in a result the caller already has.
            var batch = waiting.GetRange(0, take);

            if (take == waiting.Count)
            {
                // Removing the key rather than leaving an empty list behind is what keeps a fully drained inbox from
                // occupying a key at all.
                _inboxes.Remove(paneId);
                return new AgentInboxBatch(batch, 0);
            }

            waiting.RemoveRange(0, take);
            return new AgentInboxBatch(batch, waiting.Count);
        }
    }

    public bool Retract(string toPaneId, string messageId)
    {
        lock (_lock)
        {
            if (!_inboxes.TryGetValue(toPaneId, out var waiting))
            {
                return false;
            }

            var index = waiting.FindIndex(message => string.Equals(message.Id, messageId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            waiting.RemoveAt(index);
            if (waiting.Count == 0)
            {
                // The same reason Deliver only creates an inbox once something is going in it: a retracted delivery
                // must not leave an empty inbox behind under a pane id, which for a pane that has just closed is
                // exactly the residue the retraction exists to prevent.
                _inboxes.Remove(toPaneId);
            }

            return true;
        }
    }

    public void Forget(string paneId)
    {
        lock (_lock)
        {
            _inboxes.Remove(paneId);
        }
    }

    private static bool _IsSame(AgentMessage message, string fromPaneId, string toPaneId, string kind, string body) =>
        string.Equals(message.FromPaneId, fromPaneId, StringComparison.Ordinal)
        && string.Equals(message.ToPaneId, toPaneId, StringComparison.Ordinal)
        && string.Equals(message.Kind, kind, StringComparison.Ordinal)
        && string.Equals(message.Body, body, StringComparison.Ordinal);
}
