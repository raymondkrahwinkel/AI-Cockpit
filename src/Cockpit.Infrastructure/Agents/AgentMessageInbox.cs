using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

// AC-1013: AC-392 inbox store, one waiting-message list per recipient. Locked (not a concurrent dict, unlike
// the roster) because delivery is check-then-act — dedup and fullness checks must be atomic against two
// concurrent notifies both seeing "no duplicate" and both adding one.
internal sealed class AgentMessageInbox : IAgentMessageInbox, ISingletonService
{
    // AC-1013: cap on one pane's waiting messages, so a recipient that never reads bounds host memory and the
    // duplicate scan. Past the cap the sender is told, rather than the oldest message being silently dropped.
    internal const int MaxWaitingPerPane = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, List<AgentMessage>> _inboxes = new(StringComparer.Ordinal);

    // AC-1013: messages taken for an unreported turn (AC-394). Separate dict rather than a flag on the message,
    // because the two states allow different next actions and a flag would need every reader to remember to skip it.
    private readonly Dictionary<string, List<AgentMessage>> _inFlight = new(StringComparer.Ordinal);

    public AgentMessageDelivery Deliver(string fromPaneId, string toPaneId, string kind, string body)
    {
        lock (_lock)
        {
            _inboxes.TryGetValue(toPaneId, out var waiting);
            _inFlight.TryGetValue(toPaneId, out var inFlight);

            // AC-1013: dedup is on content, not a sender-chosen id (a resend has no id to repeat). Only unread
            // messages count — read ones aren't duplicates and can be said again — and in-flight ones count too,
            // or the window between draining a turn's batch and it landing lets the same message through twice.
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

    public AgentMessage? PeekOldest(string paneId)
    {
        lock (_lock)
        {
            return _inboxes.TryGetValue(paneId, out var waiting) && waiting.Count > 0 ? waiting[0] : null;
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

    // AC-1013: pulls named messages out of `paneId`'s in-flight list, held order preserved. Missing ids are
    // skipped, not reported — a batch a `Forget` already dropped has no outcome left to record. Call under `_lock`.
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

            // AC-1013: in-flight goes with it — its turn ended, so leaving it held would let a
            // ReturnUndelivered rebuild an inbox under a pane id no session answers to any more.
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
