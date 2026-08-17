using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Wireframe;
using Cockpit.Infrastructure.Collab;

namespace Cockpit.Infrastructure.Wireframe;

// The live coupling state behind the wireframe-access MCP (AC-872) — the third registry beside
// DiagramAccessRegistry (AC-810) and WhiteboardAccessRegistry (AC-823). Closest to the diagram, except that where
// that one parks a whole-source edit in a diff gate, this writes it through and journals it as one undoable step.
internal sealed class WireframeAccessRegistry : IWireframeAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly CouplingLedger<WireframeCoupling> _ledger = new(coupling => coupling.SessionId);
    private readonly Dictionary<string, List<HistoryEntry>> _history = new(StringComparer.Ordinal); // surfaceId -> its edits, oldest first

    public event Action<string, string>? TextChanged;

    public event Action<WireframeCouplingChange>? CouplingChanged;

    public event Action<string, string>? ComponentEdited;

    public event Action<WireframeOpenRequest>? OpenRequested;

    public event Action<string>? HistoryChanged;

    public void SurfaceOpened(string surfaceId, string name, string initialText)
    {
        WireframeCoupling? asked;
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var existing))
            {
                existing.Name = name;
                return;
            }

            _surfaces[surfaceId] = new Surface(name, initialText);

            // AC-835: this window is here because an agent asked for it, so it arrives coupled to that agent —
            // zero capabilities, like every other coupling: read and edit stay their own separate asks.
            asked = _ledger.ConsumeAwaiting(surfaceId, session => new WireframeCoupling(session, CanRead: false));
        }

        if (asked is not null)
        {
            CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, asked));
        }
    }

    public bool RequestOpen(WireframeOpenRequest request)
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
        }

        if (wasCoupled)
        {
            CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, Coupling: null));
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
            if (!_ledger.Remove(surfaceId))
            {
                return;
            }
        }

        CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, Coupling: null));
    }

    public string? PeekText(string surfaceId)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.Text : null;
        }
    }

    public IReadOnlyList<WireframeSurfaceView> ListSurfaces(string sessionId)
    {
        lock (_lock)
        {
            return _surfaces
                .Select(surface => new WireframeSurfaceView(surface.Key, surface.Value.Name, _ledger.CouplingOf(sessionId, surface.Key)))
                .ToList();
        }
    }

    public WireframeSurface? Resolve(string surfaceRef)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceRef, out var byId))
            {
                return new WireframeSurface(surfaceRef, byId.Name);
            }

            var byName = _surfaces.FirstOrDefault(surface => string.Equals(surface.Value.Name, surfaceRef, StringComparison.Ordinal));
            return byName.Key is null ? null : new WireframeSurface(byName.Key, byName.Value.Name);
        }
    }

    public WireframeCoupling? CouplingOf(string sessionId, string surfaceId)
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
        WireframeCoupling? coupling;
        lock (_lock)
        {
            coupling = _ledger.Couple(sessionId, surfaceId, _surfaces.ContainsKey(surfaceId), "Wireframe",
                session => new WireframeCoupling(session, CanRead: false));
        }

        if (coupling is not null)
        {
            CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, coupling));
        }
    }

    public void Grant(string sessionId, string surfaceId, WireframeCapability capability)
    {
        var edit = capability == WireframeCapability.Edit;
        WireframeCoupling coupling;
        lock (_lock)
        {
            if (!_surfaces.ContainsKey(surfaceId))
            {
                throw new InvalidOperationException($"Wireframe surface '{surfaceId}' is not open.");
            }

            if (_ledger.TryGet(surfaceId, out var existing))
            {
                if (existing.SessionId != sessionId)
                {
                    throw new InvalidOperationException($"Wireframe surface '{surfaceId}' is already coupled to another agent.");
                }

                // Edit implies Read — editing something you cannot see the current state of is not a narrower
                // grant, it is a confusing one.
                coupling = existing with { CanRead = true, CanEdit = existing.CanEdit || edit };
            }
            else
            {
                coupling = new WireframeCoupling(sessionId, CanRead: true, CanEdit: edit);
            }

            _ledger.Set(surfaceId, coupling);
        }

        CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, coupling));
    }

    public string? ReadCoupled(string sessionId, string surfaceId)
    {
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanRead))
            {
                return null;
            }

            return _surfaces.TryGetValue(surfaceId, out var surface) ? surface.Text : null;
        }
    }

    public void MarkRead(string sessionId, string surfaceId)
    {
        WireframeCoupling coupling;
        lock (_lock)
        {
            if (!_ledger.TryGet(surfaceId, out var existing) || existing.SessionId != sessionId || !existing.CanRead)
            {
                return;
            }

            coupling = existing with { LastReadAt = DateTimeOffset.UtcNow };
            _ledger.Set(surfaceId, coupling);
        }

        CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, coupling));
    }

    public WireframeEditResult WriteCoupled(string sessionId, string surfaceId, string text)
    {
        string summary;
        lock (_lock)
        {
            if (_Editable(sessionId, surfaceId) is not { } surface)
            {
                return WireframeEditResult.Refused(Unavailable);
            }

            var parsed = WireframeParser.Parse(text);
            if (parsed.Root is null || parsed.Errors.Count > 0)
            {
                return WireframeEditResult.Refused(
                    "That source is not one this wireframe can read back, so nothing was changed — it starts with a screen line and carries one component per line, nested with spaces.");
            }

            var before = _Lines(surface.Text);
            var after = _Lines(text);
            surface.Text = text;
            summary = $"rewrote the whole wireframe ({after.Count} line{(after.Count == 1 ? "" : "s")})";
            _Journal(surfaceId, sessionId, WireframeEditKind.Replace, "", summary, [new WireframePatch(0, null, before, after)]);
        }

        TextChanged?.Invoke(surfaceId, text);
        ComponentEdited?.Invoke(surfaceId, summary);
        HistoryChanged?.Invoke(surfaceId);
        return WireframeEditResult.Applied(summary);
    }

    // The hold check, the line surgery and the "does this still parse" gate all run inside the lock, so an edit
    // naming one component cannot be computed against a stale source and land on top of a change to another.
    // ponytail: single lock, and the parse runs under it — per-surface locks if that is ever felt.
    public WireframeEditResult EditCoupled(string sessionId, string surfaceId, WireframeComponentEdit edit)
    {
        string text, summary;
        WireframeEditKind kind;
        lock (_lock)
        {
            if (_Editable(sessionId, surfaceId) is not { } surface)
            {
                return WireframeEditResult.Refused(Unavailable);
            }

            var held = _Touches(edit).FirstOrDefault(line => surface.HeldByOperator.Contains(line));
            if (held > 0)
            {
                return WireframeEditResult.Refused(
                    $"The operator is editing the component on line {held} right now, so nothing was changed. Try the same call again once they are done with it.");
            }

            var result = WireframeComponentEditor.Apply(surface.Text, edit);
            if (result.Text is not { } edited)
            {
                return WireframeEditResult.Refused(result.Refusal ?? Unavailable);
            }

            surface.Text = text = edited;
            summary = result.Summary;
            kind = edit.Kind;
            _Journal(surfaceId, sessionId, kind, _KeyOf(edit), summary, result.Patches);
        }

        TextChanged?.Invoke(surfaceId, text);
        ComponentEdited?.Invoke(surfaceId, summary);
        HistoryChanged?.Invoke(surfaceId);
        return WireframeEditResult.Applied(summary);
    }

    // The operator's own handling takes the same read-modify-write-under-the-lock path as the agent's (AC-875), so
    // the two land beside each other instead of one overwriting the source the other was working in. The hold is not
    // checked here: it exists to keep the agent off what the operator has under their hand, not the reverse.
    public string? ApplyHandEdit(string surfaceId, WireframeComponentEdit edit)
    {
        string text, summary;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return "Dit wireframe staat niet meer open.";
            }

            var result = WireframeComponentEditor.Apply(surface.Text, edit);
            if (result.Text is not { } edited)
            {
                return result.Refusal ?? Unavailable;
            }

            surface.Text = text = edited;
            summary = result.Summary;
            _Journal(surfaceId, "operator", edit.Kind, _KeyOf(edit), summary, result.Patches);
        }

        TextChanged?.Invoke(surfaceId, text);
        ComponentEdited?.Invoke(surfaceId, summary);
        HistoryChanged?.Invoke(surfaceId);
        return null;
    }

    public IReadOnlyList<WireframeHistoryEntry> History(string surfaceId)
    {
        lock (_lock)
        {
            return _history.TryGetValue(surfaceId, out var entries)
                ? entries.Select(entry => new WireframeHistoryEntry(entry.Id, entry.Origin, entry.Kind, entry.ComponentKey, entry.Summary, entry.When, entry.Reverted)).ToList()
                : [];
        }
    }

    public string? Revert(string surfaceId, string entryId)
    {
        string text;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return "Dit wireframe staat niet meer open.";
            }

            if (!_history.TryGetValue(surfaceId, out var entries) || entries.Find(candidate => candidate.Id == entryId) is not { } entry)
            {
                return "Deze bewerking is niet gevonden.";
            }

            if (entry.Reverted)
            {
                return "Deze bewerking is al teruggedraaid.";
            }

            if (WireframeComponentEditor.Revert(surface.Text, entry.Patches, out var reverted) is { } reason)
            {
                return reason;
            }

            surface.Text = text = reverted;
            entry.Reverted = true;
        }

        TextChanged?.Invoke(surfaceId, text);
        HistoryChanged?.Invoke(surfaceId);
        return null;
    }

    public void HoldComponent(string surfaceId, int line)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var surface))
            {
                surface.HeldByOperator.Add(line);
            }
        }
    }

    public void ReleaseComponent(string surfaceId, int line)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var surface))
            {
                surface.HeldByOperator.Remove(line);
            }
        }
    }

    public bool IsHeldByOperator(string surfaceId, int line)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) && surface.HeldByOperator.Contains(line);
        }
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
            CouplingChanged?.Invoke(new WireframeCouplingChange(surfaceId, Coupling: null));
        }
    }

    private const string Unavailable = "That wireframe surface could not be edited — it may have closed or been disconnected.";

    // The surface this session may write to right now, or null. Under _lock.
    private Surface? _Editable(string sessionId, string surfaceId) =>
        _ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit
        && _surfaces.TryGetValue(surfaceId, out var surface)
            ? surface
            : null;

    // The lines an edit reaches, so a hold on any of them refuses it: the component itself, and the container it is
    // going into — dropping something into a group the operator has under their hand is their change to make too.
    private static IEnumerable<int> _Touches(WireframeComponentEdit edit) => edit.Kind switch
    {
        WireframeEditKind.Add => [edit.Parent],
        WireframeEditKind.Move => [edit.Component, edit.Parent],
        _ => [edit.Component],
    };

    private static string _KeyOf(WireframeComponentEdit edit) =>
        (edit.Kind == WireframeEditKind.Add ? edit.Parent : edit.Component).ToString();

    private static List<string> _Lines(string source) => source.ReplaceLineEndings("\n").Split('\n').ToList();

    // ponytail: unbounded for a surface's lifetime — trimmed like the strip's own row cap if that is ever felt.
    private void _Journal(string surfaceId, string origin, WireframeEditKind kind, string componentKey, string summary, IReadOnlyList<WireframePatch> patches)
    {
        var entries = _history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = [];
        entries.Add(new HistoryEntry(Guid.NewGuid().ToString("N"), origin, kind, componentKey, summary, DateTime.Now, patches));
    }

    private sealed class Surface(string name, string text)
    {
        public string Name { get; set; } = name;

        public string Text { get; set; } = text;

        // The line numbers the operator has under their hand right now (AC-841's "jij bewerkt" marking).
        public HashSet<int> HeldByOperator { get; } = [];
    }

    // AC-853's journal row. Patches carry the lines this edit put in and took out, which is what makes a targeted
    // revert possible on a format whose components have no id of their own.
    private sealed class HistoryEntry(
        string id,
        string origin,
        WireframeEditKind kind,
        string componentKey,
        string summary,
        DateTime when,
        IReadOnlyList<WireframePatch> patches)
    {
        public string Id { get; } = id;

        public string Origin { get; } = origin;

        public WireframeEditKind Kind { get; } = kind;

        public string ComponentKey { get; } = componentKey;

        public string Summary { get; } = summary;

        public DateTime When { get; } = when;

        public bool Reverted { get; set; }

        public IReadOnlyList<WireframePatch> Patches { get; } = patches;
    }
}
