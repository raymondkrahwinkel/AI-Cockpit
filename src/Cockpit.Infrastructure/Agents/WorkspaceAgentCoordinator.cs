using System.Collections.Concurrent;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;

namespace Cockpit.Infrastructure.Agents;

/// <summary>
/// The concrete roster behind <see cref="IWorkspaceAgentCoordinator"/> (AC-391): one flat set of pane ids, each
/// carrying whether that pane has agreed to be woken (AC-395) and when it last reached this server itself (AC-613).
/// A <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a locked <see cref="HashSet{T}"/>, since MCP tool
/// calls from several sessions' request threads land concurrently and none of these calls needs to observe the
/// others atomically — each only ever touches its own key.
/// </summary>
internal sealed class WorkspaceAgentCoordinator : IWorkspaceAgentCoordinator, ISingletonService
{
    /// <summary>
    /// What the roster holds about one pane. Key present = enrolled, so the host knows the pane exists; the two
    /// fields are what the pane itself has said and done. One entry rather than three dictionaries, so a pane cannot
    /// end up present in one and forgotten in another — <see cref="Forget"/> has to be able to take everything with
    /// it in a single call, and that is the property the wake consent was put here for in the first place.
    /// </summary>
    private sealed record Entry(bool WakeConsent, DateTimeOffset? LastContactUtc, DateTimeOffset? LastInboxReadUtc);

    /// <summary>
    /// How many departed panes are remembered (AC-614). Enough to cover a sender working from a listing it took a
    /// while ago on a busy desk, and small enough that this is a courtesy rather than a log — the durable record of
    /// what happened on the line is the append-only notify trail, and this is not trying to be it.
    /// </summary>
    internal const int MaxRememberedDepartures = 100;

    private readonly ConcurrentDictionary<string, Entry> _roster = new(StringComparer.Ordinal);

    // Only Forget writes here and only a refusal reads it, so one lock over both is cheaper than making two
    // concurrent collections agree with each other about which ids are still remembered.
    private readonly object _departedLock = new();
    private readonly Dictionary<string, DateTimeOffset> _departed = new(StringComparer.Ordinal);
    private readonly Queue<string> _departedOrder = new();

    // TryAdd, not an indexer assignment. Enroll runs both from the host, repeatedly, and from every cockpit-agents
    // call an agent makes — so assigning would quietly revoke an opt-in and erase a contact time on the pane's very
    // next list_agents or notify, leaving an agent that had said yes never woken and nothing anywhere saying why.
    public void Enroll(string paneId) =>
        _roster.TryAdd(paneId, new Entry(WakeConsent: false, LastContactUtc: null, LastInboxReadUtc: null));

    public bool IsEnrolled(string paneId) => _roster.ContainsKey(paneId);

    // Stamped rather than a boolean, because "when" is what a sender needs (AC-614) and "whether" falls out of it.
    // AddOrUpdate rather than TryAdd-then-assign: the wake consent lives in the same entry and must survive being
    // touched here, which is the whole reason this is one record and not three dictionaries.
    public void RecordContact(string paneId)
    {
        var now = DateTimeOffset.UtcNow;
        _roster.AddOrUpdate(
            paneId,
            _ => new Entry(WakeConsent: false, LastContactUtc: now, LastInboxReadUtc: null),
            (_, existing) => existing with { LastContactUtc = now });
    }

    public DateTimeOffset? LastContactUtc(string paneId) =>
        _roster.TryGetValue(paneId, out var entry) ? entry.LastContactUtc : null;

    // Collecting mail is contact as well — both stamps move. Turn-start delivery (AC-394) reaches this too, and
    // that is the case worth being deliberate about: the pane did not call anything, but its mail did arrive, and a
    // sender asking "will this be read" should see the route that actually works for that pane.
    public void RecordInboxRead(string paneId)
    {
        var now = DateTimeOffset.UtcNow;
        _roster.AddOrUpdate(
            paneId,
            _ => new Entry(WakeConsent: false, LastContactUtc: now, LastInboxReadUtc: now),
            (_, existing) => existing with { LastContactUtc = now, LastInboxReadUtc = now });
    }

    public DateTimeOffset? LastInboxReadUtc(string paneId) =>
        _roster.TryGetValue(paneId, out var entry) ? entry.LastInboxReadUtc : null;

    public void SetWakeConsent(string paneId, bool consents) =>
        _roster.AddOrUpdate(
            paneId,
            _ => new Entry(consents, LastContactUtc: null, LastInboxReadUtc: null),
            (_, existing) => existing with { WakeConsent = consents });

    public bool HasWakeConsent(string paneId) => _roster.TryGetValue(paneId, out var entry) && entry.WakeConsent;

    public void Forget(string paneId)
    {
        // Only a pane the roster actually held counts as having departed. Without that check every stray pane id
        // ever passed to Forget would afterwards be reported to a sender as "this pane was here and left", which is
        // the opposite of the distinction this exists to draw.
        if (!_roster.TryRemove(paneId, out _))
        {
            return;
        }

        lock (_departedLock)
        {
            // A re-departure under the same id keeps its place in the eviction order rather than being queued twice;
            // the timestamp is what moves. Otherwise one pane id cycling could push every other departure out.
            if (_departed.TryAdd(paneId, DateTimeOffset.UtcNow))
            {
                _departedOrder.Enqueue(paneId);
            }
            else
            {
                _departed[paneId] = DateTimeOffset.UtcNow;
            }

            while (_departedOrder.Count > MaxRememberedDepartures)
            {
                _departed.Remove(_departedOrder.Dequeue());
            }
        }
    }

    public DateTimeOffset? DepartedAtUtc(string paneId)
    {
        lock (_departedLock)
        {
            return _departed.TryGetValue(paneId, out var at) ? at : null;
        }
    }
}
