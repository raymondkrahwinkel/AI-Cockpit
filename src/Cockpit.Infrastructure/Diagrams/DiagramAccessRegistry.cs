using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Infrastructure.Diagrams;

// The live coupling state behind the diagram-access MCP (AC-810) — the diagram counterpart to
// TerminalAccessRegistry (AC-34), but the registry itself owns each surface's text from SurfaceOpened onward
// (a diagram is a state, not a stream), so `ReadCoupled` always returns it exactly as it stands, coupling or not.
internal sealed class DiagramAccessRegistry : IDiagramAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DiagramCoupling> _couplings = new(StringComparer.Ordinal); // surfaceId -> coupling

    public event Action<string, string>? TextChanged;

    public event Action<DiagramCouplingChange>? CouplingChanged;

    public void SurfaceOpened(string surfaceId, string name, string initialText)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var existing))
            {
                existing.Name = name;
                return;
            }

            _surfaces[surfaceId] = new Surface(name, initialText);
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
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, Coupling: null));
        }
    }

    public void UpdateText(string surfaceId, string text)
    {
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return;
            }

            surface.Text = text;
        }

        TextChanged?.Invoke(surfaceId, text);
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

        CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, Coupling: null));
    }

    public string? PeekText(string surfaceId)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.Text : null;
        }
    }

    public IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId)
    {
        lock (_lock)
        {
            return _surfaces
                .Select(surface => new DiagramSurfaceView(
                    surface.Key,
                    surface.Value.Name,
                    _couplings.TryGetValue(surface.Key, out var coupling) && coupling.SessionId == sessionId ? coupling : null))
                .ToList();
        }
    }

    public DiagramSurface? Resolve(string surfaceRef)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceRef, out var byId))
            {
                return new DiagramSurface(surfaceRef, byId.Name);
            }

            var byName = _surfaces.FirstOrDefault(surface => string.Equals(surface.Value.Name, surfaceRef, StringComparison.Ordinal));
            return byName.Key is null ? null : new DiagramSurface(byName.Key, byName.Value.Name);
        }
    }

    public DiagramCoupling? CouplingOf(string sessionId, string surfaceId)
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
        DiagramCoupling coupling;
        lock (_lock)
        {
            if (!_surfaces.ContainsKey(surfaceId))
            {
                throw new InvalidOperationException($"Diagram surface '{surfaceId}' is not open.");
            }

            if (_couplings.TryGetValue(surfaceId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Diagram surface '{surfaceId}' is already coupled to another agent.");
                }

                return; // Already coupled to this session, zero-capability or otherwise — nothing to change or announce.
            }

            coupling = new DiagramCoupling(sessionId, CanRead: false, CanEdit: false);
            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, coupling));
    }

    public void Grant(string sessionId, string surfaceId, DiagramCapability capability)
    {
        DiagramCoupling coupling;
        lock (_lock)
        {
            if (!_surfaces.ContainsKey(surfaceId))
            {
                throw new InvalidOperationException($"Diagram surface '{surfaceId}' is not open.");
            }

            if (_couplings.TryGetValue(surfaceId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Diagram surface '{surfaceId}' is already coupled to another agent.");
                }

                // Edit implies Read — editing something you cannot see the current state of is not a narrower
                // grant, it is a confusing one.
                coupling = capability == DiagramCapability.Edit
                    ? existing with { CanRead = true, CanEdit = true }
                    : existing with { CanRead = true };
            }
            else
            {
                coupling = capability == DiagramCapability.Edit
                    ? new DiagramCoupling(sessionId, CanRead: true, CanEdit: true)
                    : new DiagramCoupling(sessionId, CanRead: true, CanEdit: false);
            }

            _couplings[surfaceId] = coupling;
        }

        CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, coupling));
    }

    public string? ReadCoupled(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanRead))
            {
                return null;
            }

            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.Text : null;
        }
    }

    public bool WriteCoupled(string sessionId, string surfaceId, string text)
    {
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            surface.Text = text;
        }

        TextChanged?.Invoke(surfaceId, text);
        return true;
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
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, Coupling: null));
        }
    }

    private sealed class Surface(string name, string text)
    {
        public string Name { get; set; } = name;

        public string Text { get; set; } = text;
    }
}
