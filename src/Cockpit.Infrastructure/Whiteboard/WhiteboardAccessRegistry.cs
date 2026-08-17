using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Collab;

namespace Cockpit.Infrastructure.Whiteboard;

// The live coupling state behind the whiteboard-access MCP (AC-823) — the whiteboard counterpart to
// DiagramAccessRegistry (AC-810), but holding a rendered PNG snapshot per surface instead of text, and a write path
// (AC-854) that only ever adds: it remembers which objects each session put there, so an agent can take back its
// own and nothing else.
internal sealed class WhiteboardAccessRegistry : IWhiteboardAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly CouplingLedger<WhiteboardCoupling> _ledger = new(coupling => coupling.SessionId);
    private readonly Dictionary<string, List<HistoryEntry>> _history = new(StringComparer.Ordinal); // surfaceId -> its actions, oldest first
    private readonly Dictionary<string, List<PinEntry>> _pins = new(StringComparer.Ordinal); // surfaceId -> its pins, oldest first

    public event Action<string, byte[]>? SnapshotChanged;

    public event Action<WhiteboardCouplingChange>? CouplingChanged;

    public event Action<string, string, WhiteboardPlacement>? ObjectPlaced;

    public event Action<string, string>? ObjectErased;

    public event Action<WhiteboardOpenRequest>? OpenRequested;

    public event Action<string>? HistoryChanged;

    public event Action<string>? PinsChanged;

    public void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng)
    {
        WhiteboardCoupling? asked;
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
            asked = _ledger.ConsumeAwaiting(surfaceId, session => new WhiteboardCoupling(session, CanRead: false));
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
            _ledger.MarkAwaiting(request.SurfaceId, request.SessionId);
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
            wasCoupled = _ledger.Remove(surfaceId);
            _history.Remove(surfaceId);
            _pins.Remove(surfaceId);
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
            if (!_ledger.Remove(surfaceId))
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
                .Select(surface => new WhiteboardSurfaceView(surface.Key, surface.Value.Name, _ledger.CouplingOf(sessionId, surface.Key)))
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
            return _ledger.CouplingOf(sessionId, surfaceId);
        }
    }

    public bool IsCoupledByAnother(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            return _ledger.IsCoupledByAnother(sessionId, surfaceId);
        }
    }

    public void Couple(string sessionId, string surfaceId)
    {
        WhiteboardCoupling? coupling;
        lock (_lock)
        {
            coupling = _ledger.Couple(sessionId, surfaceId, _surfaces.ContainsKey(surfaceId), "Whiteboard",
                session => new WhiteboardCoupling(session, CanRead: false));
        }

        if (coupling is not null)
        {
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
        }
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

            if (_ledger.TryGet(surfaceId, out var existing))
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

            _ledger.Set(surfaceId, coupling);
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
    }

    public string? PlaceCoupled(string sessionId, string surfaceId, WhiteboardPlacement placement)
    {
        string objectId;
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanWrite)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return null;
            }

            objectId = Guid.NewGuid().ToString();
            surface.PlacedBy[objectId] = sessionId;
            _Journal(surfaceId, sessionId, WhiteboardHistoryKind.Place, objectId, _PlacementSummary(placement));
        }

        ObjectPlaced?.Invoke(surfaceId, objectId, placement);
        HistoryChanged?.Invoke(surfaceId);
        return objectId;
    }

    public bool ErasePlaced(string sessionId, string surfaceId, string objectId)
    {
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanWrite)
                || !_surfaces.TryGetValue(surfaceId, out var surface)
                || !(surface.PlacedBy.TryGetValue(objectId, out var placedBy) && placedBy == sessionId))
            {
                return false;
            }

            surface.PlacedBy.Remove(objectId);
            _Journal(surfaceId, sessionId, WhiteboardHistoryKind.Erase, objectId, "erased an object");
        }

        ObjectErased?.Invoke(surfaceId, objectId);
        HistoryChanged?.Invoke(surfaceId);
        return true;
    }

    private static string _PlacementSummary(WhiteboardPlacement placement) =>
        string.IsNullOrWhiteSpace(placement.Text) ? $"placed a {placement.Shape}" : $"placed a {placement.Shape} reading \"{placement.Text}\"";

    private void _Journal(string surfaceId, string origin, WhiteboardHistoryKind kind, string objectId, string summary)
    {
        var entries = _history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = [];
        entries.Add(new HistoryEntry(Guid.NewGuid().ToString("N"), origin, kind, objectId, summary, DateTime.Now));
    }

    public IReadOnlyList<WhiteboardHistoryEntry> History(string surfaceId)
    {
        lock (_lock)
        {
            return _history.TryGetValue(surfaceId, out var entries)
                ? entries.Select(entry => new WhiteboardHistoryEntry(entry.Id, entry.Origin, entry.Kind, entry.ObjectId, entry.Summary, entry.When, entry.Reverted)).ToList()
                : [];
        }
    }

    // Unlike ErasePlaced, this is the operator's own action from the strip: it reaches the object regardless of
    // whether the session that placed it is still coupled, which ErasePlaced's "still holds Write" gate would refuse.
    public string? Revert(string surfaceId, string entryId)
    {
        string objectId;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return "Dit whiteboard staat niet meer open.";
            }

            if (!_history.TryGetValue(surfaceId, out var entries) || entries.Find(candidate => candidate.Id == entryId) is not { } entry)
            {
                return "Deze bewerking is niet gevonden.";
            }

            if (entry.Kind != WhiteboardHistoryKind.Place)
            {
                return "Het terughalen van een verwijderd object kan nog niet worden teruggedraaid.";
            }

            if (entry.Reverted)
            {
                return "Deze bewerking is al teruggedraaid.";
            }

            if (!surface.PlacedBy.ContainsKey(entry.ObjectId))
            {
                return "Dit object staat niet meer op het bord.";
            }

            surface.PlacedBy.Remove(entry.ObjectId);
            entry.Reverted = true;
            objectId = entry.ObjectId;
        }

        ObjectErased?.Invoke(surfaceId, objectId);
        HistoryChanged?.Invoke(surfaceId);
        return null;
    }

    // ---- Pins (AC-849) ----

    public IReadOnlyList<WhiteboardPin> Pins(string surfaceId)
    {
        lock (_lock)
        {
            return _pins.TryGetValue(surfaceId, out var entries)
                ? entries.Select(entry => new WhiteboardPin(entry.Id, entry.ObjectId, entry.Question, entry.When, entry.Closed)).ToList()
                : [];
        }
    }

    public string AddPin(string surfaceId, string objectId, string question)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            var entries = _pins.TryGetValue(surfaceId, out var list) ? list : _pins[surfaceId] = [];
            entries.Add(new PinEntry(id, objectId, question, DateTime.Now));
        }

        PinsChanged?.Invoke(surfaceId);
        return id;
    }

    public void ClosePin(string surfaceId, string pinId)
    {
        lock (_lock)
        {
            if (!_pins.TryGetValue(surfaceId, out var entries) || entries.Find(entry => entry.Id == pinId) is not { } pin)
            {
                return;
            }

            pin.Closed = true;
        }

        PinsChanged?.Invoke(surfaceId);
    }

    public byte[]? ReadCoupled(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanRead))
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
            if (!_ledger.TryGet(surfaceId, out var existing) || existing.SessionId != sessionId || !existing.CanRead)
            {
                return;
            }

            coupling = existing with { LastReadAt = DateTimeOffset.UtcNow };
            _ledger.Set(surfaceId, coupling);
        }

        CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));
    }

    public void SessionEnded(string sessionId)
    {
        List<string> dropped;
        lock (_lock)
        {
            dropped = _ledger.RemoveAllFor(sessionId);
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

    // AC-853's journal row.
    private sealed class HistoryEntry(string id, string origin, WhiteboardHistoryKind kind, string objectId, string summary, DateTime when)
    {
        public string Id { get; } = id;

        public string Origin { get; } = origin;

        public WhiteboardHistoryKind Kind { get; } = kind;

        public string ObjectId { get; } = objectId;

        public string Summary { get; } = summary;

        public DateTime When { get; } = when;

        public bool Reverted { get; set; }
    }

    // AC-849's pin row: Closed is the operator's own call, never system-detected — there is no correlation between
    // a pin and whatever the agent later says in the session.
    private sealed class PinEntry(string id, string objectId, string question, DateTime when)
    {
        public string Id { get; } = id;

        public string ObjectId { get; } = objectId;

        public string Question { get; } = question;

        public DateTime When { get; } = when;

        public bool Closed { get; set; }
    }
}
