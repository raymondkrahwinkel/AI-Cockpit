using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// The concrete inbox store behind `IAgentMessageInbox` (AC-392): one list of waiting messages per
// recipient pane. Everything is behind one lock rather than a concurrent dictionary — unlike the roster, a
// delivery is a check-then-act (is an identical message already waiting? is the inbox full?) that has to be
// atomic, or two notifies landing on different MCP request threads at the same moment both see "no duplicate"
// and both add one. The state is small and every call is short, so a single lock is the simpler correct answer.
internal sealed class AgentMessageInbox : IAgentMessageInbox, ISingletonService
{
    // Cap on one pane's waiting messages. A recipient that never calls `read_inbox` — an agent that has this
    // server mounted but never looks — would otherwise let a neighbour on the same desk grow host memory without
    // bound, one distinct body at a time, and make the duplicate scan below linear in that. Past the cap the
    // sender is told outright rather than the oldest message being dropped: silently discarding mail the sender
    // was told had arrived is the failure this line exists to avoid.
    internal const int MaxWaitingPerPane = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<AgentMessage>> _inboxes = new(StringComparer.Ordinal);

    // Per pane, the messages taken for a turn that has not reported back yet (AC-394). Held separately from
    // `_inboxes` rather than as a flag on the message, because the two states differ in what may
    // happen next: a waiting message can be drained, retracted or deduplicated onto, and an in-flight one can
    // only be confirmed or returned. A flag would leave every reader of the waiting list responsible for
    // remembering to skip it, which is the shape of guard that holds until someone adds the next reader.
    private readonly Dictionary<string, List<AgentMessage>> _inFlight = new(StringComparer.Ordinal);

    public AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body)
    {
        lock (_lock)
        {
            _inboxes.TryGetValue(toPaneId, out var waiting);
            _inFlight.TryGetValue(toPaneId, out var inFlight);

            // Dedup is on the message's content, not on an id the sender chose: the sender re-sending because it
            // did not see an answer yet is the case this is for, and it has no id from the first attempt to repeat.
            // Only messages the recipient has not read count — one it has already read and acted on is not a
            // duplicate, and a sender must be able to say the same thing again later. An in-flight message has not
            // been read yet, so it counts too: without that, the window between taking a batch for a turn and that
            // turn landing is a window in which the same sentence gets through twice.
            var duplicate = _FirstMatch(waiting, fromPaneId, toPaneId, kind, body)
                ?? _FirstMatch(inFlight, fromPaneId, toPaneId, kind, body);
            if (duplicate is not null)
            {
                return new AgentMessageDelivery(AgentMessageDeliveryOutcome.Deduplicated, duplicate);
            }

            // Unread is unread for the cap as well: counting only the waiting list would let a sender past
            // MaxWaitingPerPane by delivering while a batch happens to be in flight.
            if ((waiting?.Count ?? 0) + (inFlight?.Count ?? 0) >= MaxWaitingPerPane)
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

    public AgentInboxBatch TakeForDelivery(string paneId, int limit)
    {
        lock (_lock)
        {
            if (!_inboxes.TryGetValue(paneId, out var waiting))
            {
                return new AgentInboxBatch([], 0);
            }

            if (limit <= 0)
            {
                return new AgentInboxBatch([], waiting.Count);
            }

            var take = Math.Min(limit, waiting.Count);
            var batch = waiting.GetRange(0, take);

            if (!_inFlight.TryGetValue(paneId, out var held))
            {
                held = [];
                _inFlight[paneId] = held;
            }

            held.AddRange(batch);

            if (take == waiting.Count)
            {
                _inboxes.Remove(paneId);
                return new AgentInboxBatch(batch, 0);
            }

            waiting.RemoveRange(0, take);
            return new AgentInboxBatch(batch, waiting.Count);
        }
    }

    public void ConfirmDelivered(string paneId, IReadOnlyList<string> messageIds)
    {
        lock (_lock)
        {
            _TakeInFlight(paneId, messageIds);
        }
    }

    public void ReturnUndelivered(string paneId, IReadOnlyList<string> messageIds)
    {
        lock (_lock)
        {
            var returning = _TakeInFlight(paneId, messageIds);
            if (returning.Count == 0)
            {
                return;
            }

            if (!_inboxes.TryGetValue(paneId, out var waiting))
            {
                waiting = [];
                _inboxes[paneId] = waiting;
            }

            // At the front, in their original order: these are older than anything that arrived while they were in
            // flight, and InsertRange keeps them in the order TakeForDelivery handed them over in. Appending would
            // reorder the recipient's mail as a side effect of a send having failed.
            waiting.InsertRange(0, returning);
        }
    }

    // Pulls the named messages out of `paneId`'s in-flight list and returns the ones that were
    // actually there, in the order they were held. Ids that are not in flight are skipped rather than reported:
    // both callers are saying what became of a batch they were handed, and a batch that a `Forget`
    // has since dropped has no outcome left to record. Call under `_lock`.
    private List<AgentMessage> _TakeInFlight(string paneId, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0 || !_inFlight.TryGetValue(paneId, out var held))
        {
            return [];
        }

        var wanted = new HashSet<string>(messageIds, StringComparer.Ordinal);
        var taken = held.Where(message => wanted.Contains(message.Id)).ToList();
        held.RemoveAll(message => wanted.Contains(message.Id));

        if (held.Count == 0)
        {
            _inFlight.Remove(paneId);
        }

        return taken;
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

            // In flight goes with it. The turn those messages were riding on belonged to the session that has just
            // ended, so there is nothing left for them to arrive in — and leaving them held would mean a
            // ReturnUndelivered from that turn's failing send could rebuild an inbox under a pane id no session
            // answers to any more, which is the residue this method exists to remove.
            _inFlight.Remove(paneId);
        }
    }

    private static AgentMessage? _FirstMatch(List<AgentMessage>? messages, string fromPaneId, string toPaneId, string kind, string body) =>
        messages?.FirstOrDefault(message => _IsSame(message, fromPaneId, toPaneId, kind, body));

    private static bool _IsSame(AgentMessage message, string fromPaneId, string toPaneId, string kind, string body) =>
        string.Equals(message.FromPaneId, fromPaneId, StringComparison.Ordinal)
        && string.Equals(message.ToPaneId, toPaneId, StringComparison.Ordinal)
        && string.Equals(message.Kind, kind, StringComparison.Ordinal)
        && string.Equals(message.Body, body, StringComparison.Ordinal);
}
