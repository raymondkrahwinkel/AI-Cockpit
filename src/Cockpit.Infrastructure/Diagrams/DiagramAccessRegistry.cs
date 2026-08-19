using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Collab;

namespace Cockpit.Infrastructure.Diagrams;

// The live coupling state behind the diagram-access MCP (AC-810) — the diagram counterpart to
// TerminalAccessRegistry (AC-34), but the registry itself owns each surface's text from SurfaceOpened onward
// (a diagram is a state, not a stream), so `ReadCoupled` always returns it exactly as it stands, coupling or not.
internal sealed class DiagramAccessRegistry : IDiagramAccessRegistry, ISingletonService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Surface> _surfaces = new(StringComparer.Ordinal);
    private readonly CouplingLedger<DiagramCoupling> _ledger = new(coupling => coupling.SessionId);
    private readonly Dictionary<string, DiagramProposal> _proposals = new(StringComparer.Ordinal); // surfaceId -> pending proposal
    private readonly Dictionary<string, List<HistoryEntry>> _history = new(StringComparer.Ordinal); // surfaceId -> its edits, oldest first
    private readonly Dictionary<string, List<PinEntry>> _pins = new(StringComparer.Ordinal); // surfaceId -> its pins, oldest first

    public event Action<string, string>? TextChanged;

    public event Action<DiagramCouplingChange>? CouplingChanged;

    public event Action<string, DiagramProposal?>? ProposalChanged;

    public event Action<string, string>? ObjectEdited;

    public event Action<string>? HistoryChanged;

    public event Action<string>? PinsChanged;

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
        bool wasCoupled, hadProposal;
        lock (_lock)
        {
            _surfaces.Remove(surfaceId);
            wasCoupled = _ledger.Remove(surfaceId);
            hadProposal = _proposals.Remove(surfaceId);
            _history.Remove(surfaceId);
            _pins.Remove(surfaceId);
        }

        if (wasCoupled)
        {
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, Coupling: null));
        }

        if (hadProposal)
        {
            ProposalChanged?.Invoke(surfaceId, null);
        }
    }

    public void UpdateText(string surfaceId, string text)
    {
        DiagramProposal? rebased;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return;
            }

            surface.Text = text;
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        _Announce(surfaceId, rebased);
    }

    public void Disconnect(string surfaceId)
    {
        bool hadProposal;
        lock (_lock)
        {
            hadProposal = _proposals.Remove(surfaceId);
            if (!_ledger.Remove(surfaceId))
            {
                if (hadProposal)
                {
                    ProposalChanged?.Invoke(surfaceId, null);
                }

                return;
            }
        }

        if (hadProposal)
        {
            ProposalChanged?.Invoke(surfaceId, null);
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
                .Select(surface => new DiagramSurfaceView(surface.Key, surface.Value.Name, _ledger.CouplingOf(sessionId, surface.Key)))
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
        DiagramCoupling? coupling;
        lock (_lock)
        {
            coupling = _ledger.Couple(sessionId, surfaceId, _surfaces.ContainsKey(surfaceId), "Diagram",
                session => new DiagramCoupling(session, CanRead: false, CanEdit: false));
        }

        if (coupling is not null)
        {
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, coupling));
        }
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

            if (_ledger.TryGet(surfaceId, out var existing))
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

            _ledger.Set(surfaceId, coupling);
        }

        CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, coupling));
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

    public bool WriteCoupled(string sessionId, string surfaceId, string text)
    {
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            surface.Text = text;
        }

        TextChanged?.Invoke(surfaceId, text);
        return true;
    }

    // The read-modify-write happens inside the lock, so an edit naming one object cannot be computed against a
    // stale copy and land on top of a change to a different one.
    // ponytail: single lock, and `edit` renders under it — per-surface locks if that is ever felt.
    public bool EditCoupled(string sessionId, string surfaceId, DiagramHandEditKind kind, string objectKey, Func<string, (string? Text, string Summary)> edit)
    {
        string text, summary;
        DiagramProposal? rebased;
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            var before = surface.Text;
            var (edited, describedAs) = edit(before);
            if (edited is null)
            {
                return false;
            }

            surface.Text = text = edited;
            summary = describedAs;
            _Journal(surfaceId, sessionId, kind, objectKey, summary, before, text);
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        ObjectEdited?.Invoke(surfaceId, summary);
        HistoryChanged?.Invoke(surfaceId);
        _Announce(surfaceId, rebased);
        return true;
    }

    // The operator's own edits take the same read-modify-write-under-the-lock path as the agent's (AC-841), so the
    // two land beside each other instead of one replacing the whole source the other was working in.
    public string? ApplyHandEdit(string surfaceId, DiagramHandEdit edit)
    {
        string text, summary;
        DiagramProposal? rebased;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return "This diagram is no longer open.";
            }

            var before = surface.Text;
            var result = DiagramObjectEdit.Apply(before, edit);
            if (result.Refusal is { } reason)
            {
                return reason;
            }

            if (CheckFidelity(result.Text!) is null)
            {
                return "This edit would leave invalid Mermaid, so nothing was changed.";
            }

            surface.Text = text = result.Text!;
            summary = result.Summary;
            _Journal(surfaceId, "operator", edit.Kind, _KeyOf(edit), summary, before, text);
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        ObjectEdited?.Invoke(surfaceId, summary);
        HistoryChanged?.Invoke(surfaceId);
        _Announce(surfaceId, rebased);
        return null;
    }

    // AC-889: the same pure function ApplyHandEdit applies, without the lock/write/journal — DiagramObjectEdit is
    // internal to this assembly, so the plugin-hosted agent tools (which need to run their own hold check around
    // the result before EditCoupled commits it) reach it through this seam instead.
    public (string? Text, string Summary, string? Refusal) ComputeHandEdit(string source, DiagramHandEdit edit)
    {
        var result = DiagramObjectEdit.Apply(source, edit);
        return (result.Text, result.Summary, result.Refusal);
    }

    // AC-853: called under the same lock the edit just landed under, so before/after is exactly what that edit
    // changed — capturing it now beats re-deriving "what would undo this" from a full-text diff at revert time.
    // ponytail: unbounded for a surface's lifetime — trimmed like the strip's own row cap if that is ever felt.
    private void _Journal(string surfaceId, string origin, DiagramHandEditKind kind, string objectKey, string summary, string before, string after)
    {
        var entries = _history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = [];
        entries.Add(new HistoryEntry(Guid.NewGuid().ToString("N"), origin, kind, objectKey, summary, DateTime.Now, _Removed(before, after)));
    }

    public IReadOnlyList<DiagramHistoryEntry> History(string surfaceId)
    {
        lock (_lock)
        {
            return _history.TryGetValue(surfaceId, out var entries)
                ? entries.Select(entry => new DiagramHistoryEntry(entry.Id, entry.Origin, entry.Kind, entry.ObjectKey, entry.Summary, entry.When, entry.Reverted)).ToList()
                : [];
        }
    }

    // Undoes one journaled edit by applying its inverse to the surface as it stands *now*, never by rewinding the
    // whole text, so it cannot discard a different object's edit made since. AddNode/Connect invert by object id
    // (robust to a later rename); RemoveNode/Disconnect/RenameNode restore the lines this edit removed.
    public string? Revert(string surfaceId, string entryId)
    {
        string text;
        DiagramProposal? rebased;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return "This diagram is no longer open.";
            }

            if (!_history.TryGetValue(surfaceId, out var entries) || entries.Find(candidate => candidate.Id == entryId) is not { } entry)
            {
                return "This edit was not found.";
            }

            if (entry.Reverted)
            {
                return "This edit has already been reverted.";
            }

            var result = entry.Kind switch
            {
                DiagramHandEditKind.AddNode => DiagramObjectEdit.RemoveNode(surface.Text, entry.ObjectKey),
                DiagramHandEditKind.Connect => _DisconnectByKey(surface.Text, entry.ObjectKey),
                DiagramHandEditKind.RenameNode => DiagramObjectEdit.RenameNode(surface.Text, entry.ObjectKey, _QuotedLabel(entry.RemovedLines) ?? entry.ObjectKey),
                DiagramHandEditKind.RelabelConnection => _RelabelByKey(surface.Text, entry.ObjectKey, _QuotedLabel(entry.RemovedLines)),
                DiagramHandEditKind.SetNodeShape => entry.RemovedLines.Count == 0
                    ? DiagramEdit.Refuse("Nothing was captured to restore.")
                    : DiagramObjectEdit.RestoreNodeShape(surface.Text, entry.ObjectKey, entry.RemovedLines[0]),
                DiagramHandEditKind.AddEntity or DiagramHandEditKind.RenameEntity or DiagramHandEditKind.SetAttribute
                    or DiagramHandEditKind.RemoveAttribute or DiagramHandEditKind.Relate
                    => DiagramObjectEdit.InvertEr(surface.Text, entry.Kind, entry.ObjectKey, entry.RemovedLines),
                _ => _Restore(surface.Text, entry.RemovedLines), // RemoveNode, Disconnect, RemoveEntity, Unrelate
            };

            if (result.Refusal is { } reason)
            {
                return reason;
            }

            if (CheckFidelity(result.Text!) is null)
            {
                return "Reverting this would leave invalid Mermaid, so nothing was changed.";
            }

            surface.Text = text = result.Text!;
            entry.Reverted = true;
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        HistoryChanged?.Invoke(surfaceId);
        _Announce(surfaceId, rebased);
        return null;
    }

    // AC-899: what an ER handling changed cannot be told from the object alone — a rename carries the name it came
    // from, an attribute the entity it sits in — so the journal key carries both halves.
    private static string _KeyOf(DiagramHandEdit edit) => edit.Kind switch
    {
        DiagramHandEditKind.Connect or DiagramHandEditKind.Disconnect or DiagramHandEditKind.RelabelConnection
            or DiagramHandEditKind.Relate or DiagramHandEditKind.Unrelate => $"{edit.Id}->{edit.To}",
        DiagramHandEditKind.SetAttribute or DiagramHandEditKind.RemoveAttribute => $"{edit.Id}.{edit.Attribute}",
        DiagramHandEditKind.RenameEntity => $"{edit.Id}>{edit.Label}",
        _ => edit.Id,
    };

    public DiagramEditSupport EditSupport(string surfaceId)
    {
        string text;
        lock (_lock)
        {
            if (!_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return new DiagramEditSupport(DiagramEditDialect.Unsupported, "This diagram is no longer open.");
            }

            text = surface.Text;
        }

        var dialect = DiagramObjectEdit.DialectOf(text);
        return new DiagramEditSupport(dialect, dialect == DiagramEditDialect.Unsupported
            ? $"Editing loose objects only works on flowchart, graph and erDiagram diagrams; this is a {DiagramObjectEdit.Keyword(text)}. Ask the coupled agent to change this diagram."
            : null);
    }

    public IReadOnlyList<DiagramErAttribute> EntityAttributes(string surfaceId, string entity)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) ? DiagramObjectEdit.Attributes(surface.Text, entity) : [];
        }
    }

    private static DiagramEdit _DisconnectByKey(string source, string objectKey) =>
        objectKey.Split("->", 2) is [var from, var to]
            ? DiagramObjectEdit.Disconnect(source, from, to)
            : DiagramEdit.Refuse("This connection can no longer be recognized.");

    private static DiagramEdit _RelabelByKey(string source, string objectKey, string? label) =>
        objectKey.Split("->", 2) is [var from, var to]
            ? DiagramObjectEdit.RelabelConnection(source, from, to, label)
            : DiagramEdit.Refuse("This connection can no longer be recognized.");

    private static DiagramEdit _Restore(string source, IReadOnlyList<string> removedLines) =>
        removedLines.Count == 0
            ? DiagramEdit.Refuse("Nothing was captured to restore.")
            : DiagramEdit.Change(string.Join("\n", _Lines(source).Concat(removedLines)), "restored");

    // The label between the first and last quote of a captured node line — safe because a label is always written
    // quoted with its own quotes escaped away (DiagramObjectEdit.Clean), so those two are never anything else.
    private static string? _QuotedLabel(IReadOnlyList<string> removedLines)
    {
        if (removedLines.Count == 0)
        {
            return null;
        }

        var line = removedLines[0];
        var first = line.IndexOf('"');
        var last = line.LastIndexOf('"');
        return first >= 0 && last > first ? line[(first + 1)..last] : null;
    }

    // Multiset subtraction, order preserved: every line `after` still has removes one matching occurrence from
    // `before`, so what is left is exactly what this edit took out — a node's definition and its connections for
    // RemoveNode, an entity's block and relationships for RemoveEntity, nothing for AddNode/Connect.
    private static List<string> _Removed(string before, string after)
    {
        var remaining = new List<string>(_Lines(before));
        foreach (var line in _Lines(after))
        {
            remaining.Remove(line);
        }

        return remaining;
    }

    private static string[] _Lines(string source) => source.ReplaceLineEndings("\n").Split('\n');

    // Takes no lock: a pure function over its argument, and its per-object-edit call-site already runs under
    // EditCoupled's own lock — a reentrant lock here would not deadlock, but would suggest a state dependency
    // that does not exist.
    public DiagramFidelity? CheckFidelity(string source)
    {
        try
        {
            return MermaidRenderPipeline.Render(source, MermaidTheme.Neutral).Fidelity;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Pins (AC-849) ----

    public IReadOnlyList<DiagramPin> Pins(string surfaceId)
    {
        lock (_lock)
        {
            return _pins.TryGetValue(surfaceId, out var entries)
                ? entries.Select(entry => new DiagramPin(entry.Id, entry.ObjectKey, entry.Question, entry.When, entry.Closed)).ToList()
                : [];
        }
    }

    public string AddPin(string surfaceId, string objectKey, string question)
    {
        var id = Guid.NewGuid().ToString("N");
        lock (_lock)
        {
            var entries = _pins.TryGetValue(surfaceId, out var list) ? list : _pins[surfaceId] = [];
            entries.Add(new PinEntry(id, objectKey, question, DateTime.Now));
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

    public void HoldObject(string surfaceId, string objectId)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var surface))
            {
                surface.HeldByOperator.Add(objectId);
            }
        }
    }

    public void ReleaseObject(string surfaceId, string objectId)
    {
        lock (_lock)
        {
            if (_surfaces.TryGetValue(surfaceId, out var surface))
            {
                surface.HeldByOperator.Remove(objectId);
            }
        }
    }

    public bool IsHeldByOperator(string surfaceId, string objectId)
    {
        lock (_lock)
        {
            return _surfaces.TryGetValue(surfaceId, out var surface) && surface.HeldByOperator.Contains(objectId);
        }
    }

    public void SessionEnded(string sessionId)
    {
        List<string> dropped, droppedProposals;
        lock (_lock)
        {
            dropped = _ledger.RemoveAllFor(sessionId);
            droppedProposals = dropped.Where(surfaceId => _proposals.Remove(surfaceId)).ToList();
        }

        foreach (var surfaceId in droppedProposals)
        {
            ProposalChanged?.Invoke(surfaceId, null);
        }

        foreach (var surfaceId in dropped)
        {
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, Coupling: null));
        }
    }

    public bool Propose(string sessionId, string surfaceId, string proposedText, string changeSummary, IReadOnlyList<string> fidelityFindings)
    {
        DiagramProposal proposal;
        lock (_lock)
        {
            if (!(_ledger.TryGet(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            var blocks = DiagramDiff.Compute(surface.Text, proposedText);
            proposal = new DiagramProposal(surfaceId, sessionId, proposedText, changeSummary, fidelityFindings, blocks);
            _proposals[surfaceId] = proposal;
        }

        ProposalChanged?.Invoke(surfaceId, proposal);
        return true;
    }

    // AC-845: since AC-841/AC-852 the source can change while a proposal waits in the gate — by hand or by the
    // agent. Applying it would then write blocks computed against the old text and silently overwrite that work,
    // so the proposal is recomputed against the text as it stands now. Under _lock.
    private DiagramProposal? _Rebase(string surfaceId, string text)
    {
        if (!_proposals.TryGetValue(surfaceId, out var proposal))
        {
            return null;
        }

        var rebased = proposal with
        {
            ChangeSummary = SourceChangeSummary.Describe(text, proposal.ProposedText),
            Blocks = DiagramDiff.Compute(text, proposal.ProposedText),
        };
        _proposals[surfaceId] = rebased;
        return rebased;
    }

    private void _Announce(string surfaceId, DiagramProposal? rebased)
    {
        if (rebased is not null)
        {
            ProposalChanged?.Invoke(surfaceId, rebased);
        }
    }

    public DiagramProposal? PendingProposal(string surfaceId)
    {
        lock (_lock)
        {
            return _proposals.TryGetValue(surfaceId, out var proposal) ? proposal : null;
        }
    }

    public bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks)
    {
        string merged;
        lock (_lock)
        {
            if (!_proposals.TryGetValue(surfaceId, out var proposal) || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            merged = DiagramDiff.Apply(proposal.Blocks, acceptedBlocks);
            surface.Text = merged;
            _proposals.Remove(surfaceId);
        }

        TextChanged?.Invoke(surfaceId, merged);
        ProposalChanged?.Invoke(surfaceId, null);
        return true;
    }

    public bool DiscardProposal(string surfaceId)
    {
        lock (_lock)
        {
            if (!_proposals.Remove(surfaceId))
            {
                return false;
            }
        }

        ProposalChanged?.Invoke(surfaceId, null);
        return true;
    }

    private sealed class Surface(string name, string text)
    {
        public string Name { get; set; } = name;

        public string Text { get; set; } = text;

        // The objects the operator has under their hand right now (AC-841's "jij bewerkt" marking).
        public HashSet<string> HeldByOperator { get; } = new(StringComparer.Ordinal);
    }

    // AC-853's journal row: RemovedLines is only ever read back for RemoveNode/Disconnect/RenameNode's inverse
    // (empty for AddNode/Connect, which invert by object id instead).
    private sealed class HistoryEntry(string id, string origin, DiagramHandEditKind kind, string objectKey, string summary, DateTime when, List<string> removedLines)
    {
        public string Id { get; } = id;

        public string Origin { get; } = origin;

        public DiagramHandEditKind Kind { get; } = kind;

        public string ObjectKey { get; } = objectKey;

        public string Summary { get; } = summary;

        public DateTime When { get; } = when;

        public bool Reverted { get; set; }

        public List<string> RemovedLines { get; } = removedLines;
    }

    // AC-849's pin row: Closed is the operator's own call, never system-detected — there is no correlation between
    // a pin and whatever the agent later says in the session.
    private sealed class PinEntry(string id, string objectKey, string question, DateTime when)
    {
        public string Id { get; } = id;

        public string ObjectKey { get; } = objectKey;

        public string Question { get; } = question;

        public DateTime When { get; } = when;

        public bool Closed { get; set; }
    }
}
