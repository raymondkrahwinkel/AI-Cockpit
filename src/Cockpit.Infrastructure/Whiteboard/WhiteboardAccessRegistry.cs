using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Infrastructure.Whiteboard;

// The live coupling state behind the whiteboard-access MCP (AC-823) — the whiteboard counterpart to
// DiagramAccessRegistry (AC-810), but holding a rendered PNG snapshot per surface instead of text, and a write path
// (AC-854) that only ever adds: it remembers which objects each session put there, so an agent can take back its
// own and nothing else.
internal sealed class WhiteboardAccessRegistry : IWhiteboardAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WhiteboardCoupling> _couplings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _awaitingCoupling = new(StringComparer.Ordinal); // surfaceId -> session that asked for it (AC-835)

    public event Action<string, byte[]>? SnapshotChanged;

    public event Action<WhiteboardCouplingChange>? CouplingChanged;

    public event Action<string, string, WhiteboardPlacement>? ObjectPlaced;

    public event Action<string, string>? ObjectErased;

    public event Action<WhiteboardOpenRequest>? OpenRequested;

    public void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng)
    {
        WhiteboardCoupling? asked = null;
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var existing))
            {
                existing.Name = name;
                return;
            }

            _surfaces[surfaceId] = new Surface(name, initialSnapshotPng);

            // AC-835: this board is here because an agent asked for it, so it arrives coupled to that agent — with
            // nothing granted: reading it and drawing on it stay their own separate asks.
            if (_awaitingCoupling.Remove(surfaceId, out var session) && !_couplings.ContainsKey(surfaceId))
            {
                _couplings[surfaceId] = asked = new WhiteboardCoupling(session, CanRead: false);
            }
        }

        if (asked is not null)
        {
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, asked));
        }
    }

    public bool RequestOpen(WhiteboardOpenRequest request)
    {
        if (OpenRequested is not { } listeners)
        {
            return false;
        }

        lock (_lock)
        {
            _awaitingCoupling[request.SurfaceId] = request.SessionId;
        }

        listeners(request);
        return true;
    }

    public void SurfaceClosed(string surfaceId)
    {
        bool wasCoupled;
        lock (_lock)
        {
            _surfaces.Remove(surfaceId);
            wasCoupled = _couplings.Remove(surfaceId);
        }

        if (wasCoupled)
        {
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, Coupling: null));
        }
    }

    public void UpdateSnapshot(string surfaceId, byte[] snapshotPng)
    {
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return;
            }

            surface.SnapshotPng = snapshotPng;
        }

        SnapshotChanged?.Invoke(surfaceId, snapshotPng);
    }

    public void Disconnect(string surfaceId)
    {
        lock (_lock)
        {
            if (!_couplings.Remove(surfaceId))
            {
                return;
            }
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, Coupling: null));
    }

    public byte[]? PeekSnapshot(string surfaceId)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.SnapshotPng : null;
        }
    }

    public IReadOnlyList<WhiteboardSurfaceView> ListSurfaces(string sessionId)
    {
        lock (_lock)
        {
            return _surfaces
                .Select(surface => new WhiteboardSurfaceView(
                    surface.Key,
                    surface.Value.Name,
                    _couplings.TryGetValue(surface.Key, out var coupling) && coupling.SessionId == sessionId ? coupling : null))
                .ToList();
        }
    }

    public WhiteboardSurface? Resolve(string surfaceRef)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceRef, out var byId))
            {
                return new WhiteboardSurface(surfaceRef, byId.Name);
            }

            var byName = _surfaces.FirstOrDefault(surface => string.Equals(surface.Value.Name, surfaceRef, StringComparison.Ordinal));
            return byName.Key is null ? null : new WhiteboardSurface(byName.Key, byName.Value.Name);
        }
    }

    public WhiteboardCoupling? CouplingOf(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId ? coupling : null;
        }
    }

    public bool IsCoupledByAnother(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            return _couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId != sessionId;
        }
    }

    public void Couple(string sessionId, string surfaceId)
    {
        WhiteboardCoupling coupling;
        lock (_lock)
        {
            if (!_surfaces.ContainsKey(surfaceId))
            {
                throw new InvalidOperationException($"Whiteboard surface '{surfaceId}' is not open.");
            }

            if (_couplings.TryGetValue(surfaceId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Whiteboard surface '{surfaceId}' is already coupled to another agent.");
                }

                return; // Already coupled to this session, zero-capability or otherwise — nothing to change or announce.
            }

            coupling = new WhiteboardCoupling(sessionId, CanRead: false);
            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
    }

    public void Grant(string sessionId, string surfaceId, WhiteboardCapability capability = WhiteboardCapability.Read)
    {
        var write = capability == WhiteboardCapability.Write;
        WhiteboardCoupling coupling;
        lock (_lock)
        {
            if (!_surfaces.ContainsKey(surfaceId))
            {
                throw new InvalidOperationException($"Whiteboard surface '{surfaceId}' is not open.");
            }

            if (_couplings.TryGetValue(surfaceId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Whiteboard surface '{surfaceId}' is already coupled to another agent.");
                }

                coupling = existing with { CanRead = true, CanWrite = existing.CanWrite || write };
            }
            else
            {
                coupling = new WhiteboardCoupling(sessionId, CanRead: true, CanWrite: write);
            }

            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
    }

    public string? PlaceCoupled(string sessionId, string surfaceId, WhiteboardPlacement placement)
    {
        string objectId;
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanWrite)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return null;
            }

            objectId = Guid.NewGuid().ToString();
            surface.PlacedBy[objectId] = sessionId;
        }

        ObjectPlaced?.Invoke(surfaceId, objectId, placement);
        return objectId;
    }

    public bool ErasePlaced(string sessionId, string surfaceId, string objectId)
    {
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanWrite)
                || !_surfaces.TryGetValue(surfaceId, out var surface)
                || !(surface.PlacedBy.TryGetValue(objectId, out var placedBy) && placedBy == sessionId))
            {
                return false;
            }

            surface.PlacedBy.Remove(objectId);
        }

        ObjectErased?.Invoke(surfaceId, objectId);
        return true;
    }

    public byte[]? ReadCoupled(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanRead))
            {
                return null;
            }

            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.SnapshotPng : null;
        }
    }

    public void MarkRead(string sessionId, string surfaceId)
    {
        WhiteboardCoupling coupling;
        lock (_lock)
        {
            if (!_couplings.TryGetValue(surfaceId, out var existing) || existing.SessionId != sessionId || !existing.CanRead)
            {
                return;
            }

            coupling = existing with { LastReadAt = DateTimeOffset.UtcNow };
            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
    }

    public void SessionEnded(string sessionId)
    {
        List<string> dropped;
        lock (_lock)
        {
            dropped = _couplings.Where(entry => entry.Value.SessionId == sessionId).Select(entry => entry.Key).ToList();
            foreach (var surfaceId in dropped)
            {
                _couplings.Remove(surfaceId);
            }
        }

        foreach (var surfaceId in dropped)
        {
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, Coupling: null));
        }
    }

    private sealed class Surface(string name, byte[] snapshotPng)
    {
        public string Name { get; set; } = name;

        public byte[] SnapshotPng { get; set; } = snapshotPng;

        // Object id -> the session that placed it. Anything not in here is the operator's (or another agent's), and
        // an agent asking to erase it is refused rather than obeyed.
        public Dictionary<string, string> PlacedBy { get; } = new(StringComparer.Ordinal);
    }
}
