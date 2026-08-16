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
    private readonly Dictionary<string, DiagramProposal> _proposals = new(StringComparer.Ordinal); // surfaceId -> pending proposal
    private readonly Dictionary<string, string> _awaitingCoupling = new(StringComparer.Ordinal); // surfaceId -> session that asked for it (AC-835)

    public event Action<string, string>? TextChanged;

    public event Action<DiagramCouplingChange>? CouplingChanged;

    public event Action<string, DiagramProposal?>? ProposalChanged;

    public event Action<string, string>? ObjectEdited;

    public event Action<DiagramOpenRequest>? OpenRequested;

    public void SurfaceOpened(string surfaceId, string name, string initialText)
    {
        DiagramCoupling? asked = null;
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
            if (_awaitingCoupling.Remove(surfaceId, out var session) && !_couplings.ContainsKey(surfaceId))
            {
                _couplings[surfaceId] = asked = new DiagramCoupling(session, CanRead: false, CanEdit: false);
            }
        }

        if (asked is not null)
        {
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, asked));
        }
    }

    public bool RequestOpen(DiagramOpenRequest request)
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
        bool wasCoupled, hadProposal;
        lock (_lock)
        {
            _surfaces.Remove(surfaceId);
            wasCoupled = _couplings.Remove(surfaceId);
            hadProposal = _proposals.Remove(surfaceId);
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
            if (!_couplings.Remove(surfaceId))
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

    // The read-modify-write happens inside the lock, so an edit naming one object cannot be computed against a
    // stale copy and land on top of a change to a different one.
    // ponytail: single lock, and `edit` renders under it — per-surface locks if that is ever felt.
    public bool EditCoupled(string sessionId, string surfaceId, Func<string, (string? Text, string Summary)> edit)
    {
        string text, summary;
        DiagramProposal? rebased;
        lock (_lock)
        {
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
                || !_surfaces.TryGetValue(surfaceId, out var surface))
            {
                return false;
            }

            var (edited, describedAs) = edit(surface.Text);
            if (edited is null)
            {
                return false;
            }

            surface.Text = text = edited;
            summary = describedAs;
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        ObjectEdited?.Invoke(surfaceId, summary);
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
                return "Dit diagram staat niet meer open.";
            }

            var result = edit.Kind switch
            {
                DiagramHandEditKind.AddNode => DiagramObjectEdit.AddNode(surface.Text, edit.Id, edit.Label ?? edit.Id),
                DiagramHandEditKind.RenameNode => DiagramObjectEdit.RenameNode(surface.Text, edit.Id, edit.Label ?? edit.Id),
                DiagramHandEditKind.RemoveNode => DiagramObjectEdit.RemoveNode(surface.Text, edit.Id),
                DiagramHandEditKind.Connect => DiagramObjectEdit.Connect(surface.Text, edit.Id, edit.To ?? "", edit.Label),
                _ => DiagramObjectEdit.Disconnect(surface.Text, edit.Id, edit.To ?? ""),
            };

            if (result.Refusal is { } reason)
            {
                return reason;
            }

            if (!_Renders(result.Text!))
            {
                return "Deze bewerking zou geen geldige Mermaid overlaten, dus er is niets veranderd.";
            }

            surface.Text = text = result.Text!;
            summary = result.Summary;
            rebased = _Rebase(surfaceId, text);
        }

        TextChanged?.Invoke(surfaceId, text);
        ObjectEdited?.Invoke(surfaceId, summary);
        _Announce(surfaceId, rebased);
        return null;
    }

    private static bool _Renders(string source)
    {
        try
        {
            MermaidRenderPipeline.Render(source, MermaidTheme.Neutral);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
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
            dropped = _couplings.Where(entry => entry.Value.SessionId == sessionId).Select(entry => entry.Key).ToList();
            foreach (var surfaceId in dropped)
            {
                _couplings.Remove(surfaceId);
            }

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
            if (!(_couplings.TryGetValue(surfaceId, out var coupling) && coupling.SessionId == sessionId && coupling.CanEdit)
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

    // AC-845: sinds AC-841/AC-852 kan de bron veranderen terwijl een voorstel in de poort wacht — met de hand of
    // door de agent. Toepassen zou dan blokken schrijven die tegen de oude tekst zijn berekend en dat werk
    // stilzwijgend overschrijven, dus wordt het voorstel herrekend tegen de tekst zoals die nu is. Onder _lock.
    private DiagramProposal? _Rebase(string surfaceId, string text)
    {
        if (!_proposals.TryGetValue(surfaceId, out var proposal))
        {
            return null;
        }

        var rebased = proposal with
        {
            ChangeSummary = DiagramChangeSummary.Describe(text, proposal.ProposedText),
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
}
