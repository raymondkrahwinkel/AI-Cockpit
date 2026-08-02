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
    private sealed record Entry(bool WakeConsent, DateTimeOffset? LastContactUtc);

    private readonly ConcurrentDictionary<string, Entry> _roster = new(StringComparer.Ordinal);

    // TryAdd, not an indexer assignment. Enroll runs both from the host, repeatedly, and from every cockpit-agents
    // call an agent makes — so assigning would quietly revoke an opt-in and erase a contact time on the pane's very
    // next list_agents or notify, leaving an agent that had said yes never woken and nothing anywhere saying why.
    public void Enroll(string paneId) => _roster.TryAdd(paneId, new Entry(WakeConsent: false, LastContactUtc: null));

    public bool IsEnrolled(string paneId) => _roster.ContainsKey(paneId);

    // Stamped rather than a boolean, because "when" is what a sender needs (AC-614) and "whether" falls out of it.
    // AddOrUpdate rather than TryAdd-then-assign: the wake consent lives in the same entry and must survive being
    // touched here, which is the whole reason this is one record and not two dictionaries.
    public void RecordContact(string paneId)
    {
        var now = DateTimeOffset.UtcNow;
        _roster.AddOrUpdate(
            paneId,
            _ => new Entry(WakeConsent: false, LastContactUtc: now),
            (_, existing) => existing with { LastContactUtc = now });
    }

    public DateTimeOffset? LastContactUtc(string paneId) =>
        _roster.TryGetValue(paneId, out var entry) ? entry.LastContactUtc : null;

    public void SetWakeConsent(string paneId, bool consents) =>
        _roster.AddOrUpdate(
            paneId,
            _ => new Entry(consents, LastContactUtc: null),
            (_, existing) => existing with { WakeConsent = consents });

    public bool HasWakeConsent(string paneId) => _roster.TryGetValue(paneId, out var entry) && entry.WakeConsent;

    public void Forget(string paneId) => _roster.TryRemove(paneId, out _);
}
