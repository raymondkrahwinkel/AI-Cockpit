using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Whiteboard;

namespace Cockpit.Infrastructure.Whiteboard;

// The live coupling state behind the whiteboard-access MCP (AC-823) — the whiteboard counterpart to
// DiagramAccessRegistry (AC-810), but holding a rendered PNG snapshot per surface instead of text, and only the one
// Read capability — there is no edit_whiteboard to widen into.
internal sealed class WhiteboardAccessRegistry : IWhiteboardAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WhiteboardCoupling> _couplings = new(StringComparer.Ordinal);

    public event Action<string, byte[]>? SnapshotChanged;

    public event Action<WhiteboardCouplingChange>? CouplingChanged;

    public void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var existing))
            {
                existing.Name = name;
                return;
            }

            _surfaces[surfaceId] = new Surface(name, initialSnapshotPng);
        }
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

    public void Grant(string sessionId, string surfaceId)
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

                coupling = existing with { CanRead = true };
            }
            else
            {
                coupling = new WhiteboardCoupling(sessionId, CanRead: true);
            }

            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
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
    }
}
