using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Diagrams;

// The `cockpit-diagram` MCP tools (AC-810), gated per-capability like `cockpit-terminal` (AC-34) — read that class
// first. Deviations: `read_diagram` always returns the surface as it stands (a state, not a stream), and
// `edit_diagram`'s consent text is derived from the real change (DiagramChangeSummary), never agent prose (cf. AC-489).
// The per-object tools (AC-852) share Edit's one ask but write straight through: only `edit_diagram` still diffs.
internal sealed class DiagramMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IDiagramAccessRegistry _registry;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real access is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public DiagramMcpTools(IDiagramAccessRegistry registry, IConsentBroker? consent = null)
    {
        _registry = registry;
        _consent = consent;
    }

    [McpServerTool(Name = "list_diagrams")]
    [Description("Lists the diagram surfaces the operator has open that you could ask to use: each with a stable id, the name the operator sees, and whether you already hold read/edit on it. Reading or editing a surface needs the operator to approve it first (see read_diagram / edit_diagram); this list only names the surfaces so you can reference one. A surface can be coupled to you with neither capability yet — that is a real, valid state, not an error.")]
    public string ListDiagrams(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var diagrams = _registry.ListSurfaces(caller)
            .Select(surface => new
            {
                id = surface.SurfaceId,
                name = surface.Name,
                canRead = surface.Coupling?.CanRead ?? false,
                canEdit = surface.Coupling?.CanEdit ?? false,
            });
        return _Serialize(new { ok = true, diagrams });
    }

    [McpServerTool(Name = "read_diagram")]
    [Description("Returns a diagram surface's Mermaid source — you name it by the id or name from list_diagrams. The first time you read a surface the operator gets an Approve/Deny prompt naming which diagram and how big it is; only after Approve do you get its source, and it is the surface exactly as it stands now, including anything the operator put there before you connected. Reading does not let you edit — edit_diagram asks for that separately. Also reports whether the render engine would drop anything from this source (see `fidelity`) — describe the diagram as incomplete if it does.")]
    public async Task<string> ReadDiagram(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to read, by its id or name from list_diagrams.")] string diagram)
    {
        if (_registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Read).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var source = _registry.ReadCoupled(caller, surface.SurfaceId) ?? "";
        var fidelity = _ComputeFidelity(source);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            source,
            fidelity = new { complete = fidelity.IsComplete, findings = fidelity.Findings },
        });
    }

    [McpServerTool(Name = "edit_diagram")]
    [Description("Proposes replacing a diagram surface's Mermaid source with `source` — you name the surface by the id or name from list_diagrams. Needs its own Approve, asked the first time you edit a surface (covering read too, in one prompt) or as a widening prompt if you were only reading it before — that approval lets you propose edits, it does not apply this one. The proposal appears in the diagram panel as a diff, block by block, for the operator to accept or reject; nothing reaches the stored source until they do (AC-825). The operator's prompt shows how many lines change, computed from the actual edit — not from anything you write here, so there is nothing to word carefully. Also reports whether the render engine would drop anything from the proposed source (see `fidelity`) — the operator sees this on the proposal itself, before deciding.")]
    public async Task<string> EditDiagram(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The full replacement Mermaid source for this diagram.")] string source)
    {
        if (_registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        var changeSummary = DiagramChangeSummary.Describe(_registry.PeekText(surface.SurfaceId) ?? "", source);
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Edit, changeSummary).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var fidelity = _ComputeFidelity(source);
        if (!_registry.Propose(caller, surface.SurfaceId, source, changeSummary, fidelity.Findings))
        {
            return _Serialize(new { ok = false, error = "That diagram surface could not accept a proposal — it may have closed or been disconnected." });
        }

        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            proposed = true,
            changeSummary,
            fidelity = new { complete = fidelity.IsComplete, findings = fidelity.Findings },
        });
    }

    [McpServerTool(Name = "add_node")]
    [Description("Adds one node to a diagram surface and applies it straight away — the rest of the diagram is left exactly as it is, including anything the operator changed since you last read it. `id` is how connections refer to the node (letters, digits, underscores); `label` is what is drawn in it. Needs the same one-off Approve as edit_diagram, and is refused with a reason if a node with that id is already there, or if the operator is editing that object right now — try it again once they let go.")]
    public Task<string> AddNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The new node's id: one word of letters, digits or underscores.")] string id,
        [Description("The text drawn inside the node.")] string label) =>
        _ApplyObjectEditAsync(session, diagram, $"add node \"{_SingleLine(label)}\"", [id],
            source => DiagramObjectEdit.AddNode(source, id, label));

    [McpServerTool(Name = "rename_node")]
    [Description("Changes one node's label and applies it straight away, leaving every other line of the diagram alone. The node's id stays as it is — that is what its connections are written in terms of, so renaming the label never rewrites them. Refused with a reason if there is no such node, or if the operator is editing that node right now.")]
    public Task<string> RenameNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node to rename, as it appears in the source.")] string id,
        [Description("The new text to draw inside the node.")] string label) =>
        _ApplyObjectEditAsync(session, diagram, $"rename node {_SingleLine(id)} to \"{_SingleLine(label)}\"", [id],
            source => DiagramObjectEdit.RenameNode(source, id, label));

    [McpServerTool(Name = "remove_node")]
    [Description("Removes one node and the connections that ran to or from it — nothing else. A connection whose node is gone would draw that node again on the next render, which is why they go together; the reply says how many went with it. Refused with a reason if there is no such node, or if the operator is editing it right now.")]
    public Task<string> RemoveNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node to remove.")] string id) =>
        _ApplyObjectEditAsync(session, diagram, $"remove node {_SingleLine(id)} and its connections", [id],
            source => DiagramObjectEdit.RemoveNode(source, id));

    [McpServerTool(Name = "connect_nodes")]
    [Description("Draws one connection from one node to another and applies it straight away, leaving the rest of the diagram alone. An id that is not in the diagram yet becomes a node of its own, the way Mermaid reads it — use add_node first if you want it to carry a label. Refused with a reason if that connection is already there, or if the operator is editing either end (or the connection itself) right now.")]
    public Task<string> ConnectNodes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node the connection starts at.")] string from,
        [Description("The id of the node the connection ends at.")] string to,
        [Description("Optional text drawn on the connection.")] string? label = null) =>
        _ApplyObjectEditAsync(session, diagram, $"connect {_SingleLine(from)} -> {_SingleLine(to)}", [from, to, $"{from}->{to}"],
            source => DiagramObjectEdit.Connect(source, from, to, label));

    [McpServerTool(Name = "disconnect_nodes")]
    [Description("Removes one connection between two nodes and applies it straight away. Both nodes stay; only the line between them goes. Refused with a reason if there is no such connection, or if the operator is editing either end (or the connection itself) right now.")]
    public Task<string> DisconnectNodes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node the connection starts at.")] string from,
        [Description("The id of the node the connection ends at.")] string to) =>
        _ApplyObjectEditAsync(session, diagram, $"disconnect {_SingleLine(from)} -> {_SingleLine(to)}", [from, to, $"{from}->{to}"],
            source => DiagramObjectEdit.Disconnect(source, from, to));

    // The one path every per-object tool takes (AC-852). Same Edit consent as edit_diagram, then the edit itself
    // runs inside the registry's lock: the hold check, the line surgery and the "is this still valid Mermaid"
    // render all see one text, and nothing is written unless all three pass.
    private async Task<string> _ApplyObjectEditAsync(
        string session,
        string diagram,
        string ask,
        string[] objects,
        Func<string, DiagramEdit> edit)
    {
        if (_registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Edit, ask).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        string? refusal = null;
        var summary = "";
        var fidelity = new DiagramFidelity([]);
        var applied = _registry.EditCoupled(caller, surface.SurfaceId, current =>
        {
            if (objects.FirstOrDefault(name => _registry.IsHeldByOperator(surface.SurfaceId, name)) is { } held)
            {
                refusal = $"The operator is editing \"{held}\" right now, so nothing was changed. Try the same call again once they are done with it.";
                return (null, "");
            }

            var result = edit(current);
            if (result.Refusal is { } reason)
            {
                refusal = reason;
                return (null, "");
            }

            if (!_TryRender(result.Text!, out fidelity))
            {
                refusal = "That change would not have left valid Mermaid behind, so nothing was changed.";
                return (null, "");
            }

            summary = result.Summary;
            return (result.Text, result.Summary);
        });

        if (!applied)
        {
            return _Serialize(new
            {
                ok = false,
                error = refusal ?? "That diagram surface could not be edited — it may have closed or been disconnected.",
            });
        }

        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            changed = summary,
            fidelity = new { complete = fidelity.IsComplete, findings = fidelity.Findings },
        });
    }

    // Ensures this session holds at least `needed` on `surface`, asking the operator once for exactly that much.
    // Returns an error string to surface, or null when the session now holds it. `changeSummary` is only meaningful
    // (and only supplied) for an Edit ask — Read has nothing of the caller's to describe.
    private async Task<string?> _EnsureCapabilityAsync(string caller, DiagramSurface surface, DiagramCapability needed, string? changeSummary = null)
    {
        var held = _registry.CouplingOf(caller, surface.SurfaceId);
        if (needed == DiagramCapability.Read && held is { CanRead: true })
        {
            return null;
        }

        if (needed == DiagramCapability.Edit && held is { CanEdit: true })
        {
            return null;
        }

        if (held is null && _registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Diagram \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        if (_consent is null)
        {
            return "Using a diagram surface needs the operator's approval, which is not available here.";
        }

        // Widening applies only to the read-then-edit path: granting Edit always grants Read alongside it, so
        // there is no "held Edit, now wants Read" case, and a fresh zero-capability coupling asking for Read is a
        // first ask, not a widening of anything.
        var widening = needed == DiagramCapability.Edit && held is { CanRead: true };
        var decision = await _consent.RequestConsentAsync(_PromptFor(surface, needed, widening, changeSummary)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return needed == DiagramCapability.Read
                ? "Reading that diagram was not approved by the operator."
                : "Editing that diagram was not approved by the operator — you may still be able to read it.";
        }

        try
        {
            _registry.Couple(caller, surface.SurfaceId);
            _registry.Grant(caller, surface.SurfaceId, needed);
        }
        catch (InvalidOperationException)
        {
            // The operator was deciding for as long as they took, and the world moved: another agent got the
            // surface first, or it closed.
            return $"Diagram \"{surface.Name}\" is no longer available — another agent took it, or it closed while the operator was deciding.";
        }

        return null;
    }

    // Read's prompt names the diagram and its size, because a snapshot read hands over everything already in it
    // (AC-810's deviation from AC-34: there is no "since the coupling" to lean on). Edit's prompt states the change
    // itself, mechanically derived (DiagramChangeSummary), never text the calling agent composed.
    private static ConsentRequest _PromptFor(DiagramSurface surface, DiagramCapability needed, bool widening, string? changeSummary) =>
        needed == DiagramCapability.Read
            ? new ConsentRequest(
                "An agent wants to read a diagram",
                $"Let this agent read diagram \"{_SingleLine(surface.Name)}\" exactly as it stands now — including everything already in it. It cannot change it: that is a separate question, asked separately.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.DiagramMcp),
                "diagram.read",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                widening
                    ? "An agent that is reading a diagram now wants to edit it"
                    : "An agent wants to read a diagram and edit it",
                $"Let this agent edit diagram \"{_SingleLine(surface.Name)}\" ({changeSummary}). A change to a single node or connection is applied straight away; replacing the whole source is offered to you as a proposal, block by block, in the diagram panel. You can watch, edit alongside, and Disconnect at any time.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.DiagramMcp),
                "diagram.edit",
                ConsentRisk.Dangerous);

    // Fold anything a consent surface could render as a line break out of the diagram name before it goes verbatim
    // into the Dangerous prompt (cf. TerminalMcpTools, AC-80/AC-92).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    // The render engine is fed agent-supplied text it may not be able to parse at all — that must not crash the
    // MCP call, only be reported as an unverifiable fidelity rather than a false "complete".
    private static DiagramFidelity _ComputeFidelity(string source) =>
        _TryRender(source, out var fidelity)
            ? fidelity
            : new DiagramFidelity(["Could not check this diagram against the render engine — the source may not be valid Mermaid syntax."]);

    // False when the engine could not render this source at all — a per-object edit that produced that is not
    // written (AC-852: every call leaves valid Mermaid behind).
    private static bool _TryRender(string source, out DiagramFidelity fidelity)
    {
        try
        {
            fidelity = MermaidRenderPipeline.Render(source, MermaidTheme.Neutral).Fidelity;
            return true;
        }
        catch (Exception)
        {
            fidelity = new DiagramFidelity([]);
            return false;
        }
    }

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
