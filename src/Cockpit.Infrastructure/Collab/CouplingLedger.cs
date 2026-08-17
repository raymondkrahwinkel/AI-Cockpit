namespace Cockpit.Infrastructure.Collab;

// The payload-independent half of a collab registry (AC-870): which session is coupled to which surface. Generic
// over the coupling record because DiagramCoupling/WhiteboardCoupling differ in capability shape. Not thread-safe
// on its own — every registry composing this already guards each call with its own lock.
internal sealed class CouplingLedger<TCoupling>(Func<TCoupling, string> sessionIdOf)
{
    private readonly Dictionary<string, TCoupling> _couplings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _awaitingCoupling = new(StringComparer.Ordinal);

    public bool TryGet(string surfaceId, out TCoupling coupling) => _couplings.TryGetValue(surfaceId, out coupling!);

    public void Set(string surfaceId, TCoupling coupling) => _couplings[surfaceId] = coupling;

    public TCoupling? CouplingOf(string sessionId, string surfaceId) =>
        _couplings.TryGetValue(surfaceId, out var coupling) && sessionIdOf(coupling) == sessionId ? coupling : default;

    public bool IsCoupledByAnother(string sessionId, string surfaceId) =>
        _couplings.TryGetValue(surfaceId, out var coupling) && sessionIdOf(coupling) != sessionId;

    // Throws exactly like Couple() on both registries did: unknown surface, or coupled to someone else already.
    // Returns null when this session already held the (possibly zero-capability) coupling — nothing changed,
    // nothing for the caller to announce.
    public TCoupling? Couple(string sessionId, string surfaceId, bool surfaceExists, string surfaceKind, Func<string, TCoupling> zeroCapability)
    {
        if (!surfaceExists)
        {
            throw new InvalidOperationException($"{surfaceKind} surface '{surfaceId}' is not open.");
        }

        if (_couplings.TryGetValue(surfaceId, out var existing))
        {
            if (sessionIdOf(existing) != sessionId)
            {
                throw new InvalidOperationException($"{surfaceKind} surface '{surfaceId}' is already coupled to another agent.");
            }

            return default;
        }

        var coupling = zeroCapability(sessionId);
        _couplings[surfaceId] = coupling;
        return coupling;
    }

    // SurfaceOpened's "this window is here because an agent asked for it" hand-off (AC-835): pops the awaiting
    // entry and creates a zero-capability coupling for it, once, the first time the surface registers.
    public TCoupling? ConsumeAwaiting(string surfaceId, Func<string, TCoupling> zeroCapability)
    {
        if (!_awaitingCoupling.Remove(surfaceId, out var session) || _couplings.ContainsKey(surfaceId))
        {
            return default;
        }

        var coupling = zeroCapability(session);
        _couplings[surfaceId] = coupling;
        return coupling;
    }

    public void MarkAwaiting(string surfaceId, string sessionId) => _awaitingCoupling[surfaceId] = sessionId;

    public bool Remove(string surfaceId) => _couplings.Remove(surfaceId);

    // SessionEnded: every surface this session held anything on, coupling dropped.
    public List<string> RemoveAllFor(string sessionId)
    {
        var dropped = _couplings.Where(entry => sessionIdOf(entry.Value) == sessionId).Select(entry => entry.Key).ToList();
        foreach (var surfaceId in dropped)
        {
            _couplings.Remove(surfaceId);
        }

        return dropped;
    }
}
