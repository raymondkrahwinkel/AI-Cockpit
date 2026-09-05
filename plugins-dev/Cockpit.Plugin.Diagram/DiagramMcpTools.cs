using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Consent;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Diagram;

// The `cockpit-diagram` MCP tools (AC-810), gated per-capability like `cockpit-terminal` (AC-34) — read that class
// first. Deviations: `read_diagram` returns the surface as it stands (a state, not a stream), `edit_diagram`'s
// consent text comes from SourceChangeSummary (AC-489), and the per-object tools (AC-852) write straight through.
internal sealed class DiagramMcpTools(ICockpitHost host, IDiagramAccessRegistry registry, DiagramSettings settings)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    [McpServerTool(Name = "list_diagrams", ReadOnly = true)]
    [Description("Lists the diagram surfaces the operator has open that you could ask to use: each with a stable id, the name the operator sees, and whether you already hold read/edit on it. Reading or editing a surface needs the operator to approve it first (see read_diagram / edit_diagram); this list only names the surfaces so you can reference one. A surface can be coupled to you with neither capability yet — that is a real, valid state, not an error.")]
    public string ListDiagrams(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = host.CurrentMcpCallerPaneId ?? session;
        var diagrams = registry.ListSurfaces(caller)
            .Select(surface => new
            {
                id = surface.SurfaceId,
                name = surface.Name,
                canRead = surface.Coupling?.CanRead ?? false,
                canEdit = surface.Coupling?.CanEdit ?? false,
            });
        return _Serialize(new { ok = true, diagrams });
    }

    [McpServerTool(Name = "open_diagram", ReadOnly = false, Destructive = false)]
    [Description("Asks the operator to put a diagram YOU wrote on their screen, so the two of you can go through it together — this is how you show a diagram nobody has open yet, rather than waiting for the operator to make one for you. The operator gets an Approve/Deny prompt naming the diagram and how big it is; on Approve a diagram window opens beside the cockpit with your Mermaid source in it, coupled to you. On Deny nothing opens at all. The coupling on its own grants nothing: reading the surface back, or editing it afterwards, still ask their own approval (read_diagram / edit_diagram). Refused without asking if the source is not something the render engine can draw.")]
    public async Task<string> OpenDiagram(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The name the operator sees on the window — say what the diagram is about.")] string name,
        [Description("The Mermaid source to open it with.")] string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return _Serialize(new { ok = false, error = "Give the Mermaid source of the diagram you want to go through — an empty diagram is nothing to discuss." });
        }

        if (registry.CheckFidelity(source) is not { } fidelity)
        {
            return _Serialize(new { ok = false, error = "The render engine cannot draw that source, so nothing was opened — check the Mermaid syntax first." });
        }

        var title = string.IsNullOrWhiteSpace(name) ? "Diagram" : name.Trim();
        var surfaceId = Guid.NewGuid().ToString("n");
        var caller = host.CurrentMcpCallerPaneId ?? session;

        // AC-948: the operator's own opt-out, set on this plugin's settings page — off by default.
        if (!settings.SkipDiagramConsent)
        {
            var decision = await host.RequestConsentAsync(new ConsentRequest(
                "An agent wants to open a diagram to go through with you",
                $"Open a diagram window \"{_SingleLine(title)}\" beside the cockpit, holding {source.Split('\n').Length} lines of Mermaid this agent wrote, and couple that agent to it. It cannot read the surface back or change it afterwards without asking you separately.",
                new ConsentSource(surfaceId, null, ConsentSourceCatalog.DiagramMcp),
                "diagram.open",
                ConsentRisk.Dangerous)).ConfigureAwait(false);

            if (!decision.IsApproved)
            {
                return _Serialize(new { ok = false, error = "Opening that diagram was not approved by the operator — nothing was opened." });
            }
        }

        Dispatcher.UIThread.Post(() =>
            _ = DiagramWindow.OpenAsync(host, new DiagramDocument(surfaceId, title, source), caller));

        return _Serialize(new
        {
            ok = true,
            id = surfaceId,
            name = title,
            opened = true,
            fidelity = new { complete = fidelity.IsComplete, findings = fidelity.Findings },
        });
    }

    [McpServerTool(Name = "read_diagram", ReadOnly = true)]
    [Description("Returns a diagram surface's Mermaid source — you name it by the id or name from list_diagrams. The first time you read a surface the operator gets an Approve/Deny prompt naming which diagram and how big it is; only after Approve do you get its source, and it is the surface exactly as it stands now, including anything the operator put there before you connected. Reading does not let you edit — edit_diagram asks for that separately. Also reports whether the render engine would drop anything from this source (see `fidelity`) — describe the diagram as incomplete if it does.")]
    public async Task<string> ReadDiagram(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to read, by its id or name from list_diagrams.")] string diagram)
    {
        if (registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Read).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var source = registry.ReadCoupled(caller, surface.SurfaceId) ?? "";
        var fidelity = registry.CheckFidelity(source)
            ?? new DiagramFidelity(["Could not check this diagram against the render engine — the source may not be valid Mermaid syntax."]);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            source,
            fidelity = new { complete = fidelity.IsComplete, findings = fidelity.Findings },
        });
    }

    [McpServerTool(Name = "edit_diagram", ReadOnly = false, Destructive = false)]
    [Description("Proposes replacing a diagram surface's Mermaid source with `source` — you name the surface by the id or name from list_diagrams. Needs its own Approve, asked the first time you edit a surface (covering read too, in one prompt) or as a widening prompt if you were only reading it before — that approval lets you propose edits, it does not apply this one. The proposal appears in the diagram panel as a diff, block by block, for the operator to accept or reject; nothing reaches the stored source until they do (AC-825). The operator's prompt shows how many lines change, computed from the actual edit — not from anything you write here, so there is nothing to word carefully. Also reports whether the render engine would drop anything from the proposed source (see `fidelity`) — the operator sees this on the proposal itself, before deciding.")]
    public async Task<string> EditDiagram(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The full replacement Mermaid source for this diagram.")] string source)
    {
        if (registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        var changeSummary = SourceChangeSummary.Describe(registry.PeekText(surface.SurfaceId) ?? "", source);
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Edit, changeSummary).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var fidelity = registry.CheckFidelity(source)
            ?? new DiagramFidelity(["Could not check this diagram against the render engine — the source may not be valid Mermaid syntax."]);
        if (!registry.Propose(caller, surface.SurfaceId, source, changeSummary, fidelity.Findings))
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

    [McpServerTool(Name = "add_node", ReadOnly = false, Destructive = false)]
    [Description("Adds one node to a flowchart/graph surface and applies it straight away (an erDiagram uses add_entity instead) — the rest of the diagram is left exactly as it is, including anything the operator changed since you last read it. `id` is how connections refer to the node (letters, digits, underscores); `label` is what is drawn in it. Needs the same one-off Approve as edit_diagram, and is refused with a reason if a node with that id is already there, or if the operator is editing that object right now — try it again once they let go.")]
    public Task<string> AddNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The new node's id: one word of letters, digits or underscores.")] string id,
        [Description("The text drawn inside the node.")] string label) =>
        _ApplyObjectEditAsync(session, diagram, $"add node \"{_SingleLine(label)}\"", id, [id],
            new DiagramHandEdit(DiagramHandEditKind.AddNode, id, Label: label));

    [McpServerTool(Name = "rename_node", ReadOnly = false, Destructive = false)]
    [Description("Changes one node's label and applies it straight away, leaving every other line of the diagram alone. The node's id stays as it is — that is what its connections are written in terms of, so renaming the label never rewrites them. Refused with a reason if there is no such node, or if the operator is editing that node right now.")]
    public Task<string> RenameNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node to rename, as it appears in the source.")] string id,
        [Description("The new text to draw inside the node.")] string label) =>
        _ApplyObjectEditAsync(session, diagram, $"rename node {_SingleLine(id)} to \"{_SingleLine(label)}\"", id, [id],
            new DiagramHandEdit(DiagramHandEditKind.RenameNode, id, Label: label));

    [McpServerTool(Name = "remove_node", ReadOnly = false, Destructive = false)]
    [Description("Removes one node and the connections that ran to or from it — nothing else. A connection whose node is gone would draw that node again on the next render, which is why they go together; the reply says how many went with it. Refused with a reason if there is no such node, or if the operator is editing it right now.")]
    public Task<string> RemoveNode(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node to remove.")] string id) =>
        _ApplyObjectEditAsync(session, diagram, $"remove node {_SingleLine(id)} and its connections", id, [id],
            new DiagramHandEdit(DiagramHandEditKind.RemoveNode, id));

    [McpServerTool(Name = "connect_nodes", ReadOnly = false, Destructive = false)]
    [Description("Draws one connection from one node to another on a flowchart/graph surface and applies it straight away, leaving the rest of the diagram alone (an erDiagram uses relate_entities instead). An id that is not in the diagram yet becomes a node of its own, the way Mermaid reads it — use add_node first if you want it to carry a label. Refused with a reason if that connection is already there, or if the operator is editing either end (or the connection itself) right now.")]
    public Task<string> ConnectNodes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node the connection starts at.")] string from,
        [Description("The id of the node the connection ends at.")] string to,
        [Description("Optional text drawn on the connection.")] string? label = null) =>
        _ApplyObjectEditAsync(session, diagram, $"connect {_SingleLine(from)} -> {_SingleLine(to)}", $"{from}->{to}", [from, to, $"{from}->{to}"],
            new DiagramHandEdit(DiagramHandEditKind.Connect, from, To: to, Label: label));

    [McpServerTool(Name = "disconnect_nodes", ReadOnly = false, Destructive = false)]
    [Description("Removes one connection between two nodes and applies it straight away. Both nodes stay; only the line between them goes. Refused with a reason if there is no such connection, or if the operator is editing either end (or the connection itself) right now.")]
    public Task<string> DisconnectNodes(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node the connection starts at.")] string from,
        [Description("The id of the node the connection ends at.")] string to) =>
        _ApplyObjectEditAsync(session, diagram, $"disconnect {_SingleLine(from)} -> {_SingleLine(to)}", $"{from}->{to}", [from, to, $"{from}->{to}"],
            new DiagramHandEdit(DiagramHandEditKind.Disconnect, from, To: to));

    [McpServerTool(Name = "relabel_connection", ReadOnly = false, Destructive = false)]
    [Description("Changes, sets or clears the label drawn on one existing connection between two nodes on a flowchart/graph surface, applied straight away and leaving the rest of the diagram alone. Leave `label` out (or empty) to remove the label entirely. Refused with a reason if there is no such connection, or if the operator is editing either end (or the connection itself) right now.")]
    public Task<string> RelabelConnection(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node the connection starts at.")] string from,
        [Description("The id of the node the connection ends at.")] string to,
        [Description("The new label text, or leave it out to remove the label.")] string? label = null) =>
        _ApplyObjectEditAsync(session, diagram, $"relabel {_SingleLine(from)} -> {_SingleLine(to)}", $"{from}->{to}", [from, to, $"{from}->{to}"],
            new DiagramHandEdit(DiagramHandEditKind.RelabelConnection, from, To: to, Label: label));

    [McpServerTool(Name = "set_node_shape", ReadOnly = false, Destructive = false)]
    [Description("Changes one node's shape on a flowchart/graph surface, applied straight away and leaving its label and every other line alone. Refused with a reason if there is no such node, the shape name is not recognized, or the operator is editing that node right now.")]
    public Task<string> SetNodeShape(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The id of the node to reshape.")] string id,
        [Description("The new shape: rectangle, rounded, diamond, stadium or subroutine.")] string shape)
    {
        if (_Shape(shape) is not { } value)
        {
            return Task.FromResult(_Serialize(new { ok = false, error = "A shape must be one of: rectangle, rounded, diamond, stadium, subroutine." }));
        }

        return _ApplyObjectEditAsync(session, diagram, $"change node {_SingleLine(id)} shape to {shape}", id, [id],
            new DiagramHandEdit(DiagramHandEditKind.SetNodeShape, id) { Shape = value });
    }

    [McpServerTool(Name = "add_entity", ReadOnly = false, Destructive = false)]
    [Description("Adds one entity to an erDiagram surface and applies it straight away, leaving the rest of the diagram alone — this is the ER counterpart of add_node, and the two are not interchangeable: an entity has no label of its own, its name is what is drawn in it. The entity arrives with an empty attribute block; set_attribute fills it. Refused with a reason if the diagram is not an erDiagram, if that entity is already there, or if the operator is editing it right now.")]
    public Task<string> AddEntity(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The new entity's name: one word of letters, digits or underscores. It is also what is drawn in the box.")] string entity) =>
        _ApplyObjectEditAsync(session, diagram, $"add entity {_SingleLine(entity)}", entity, [entity],
            new DiagramHandEdit(DiagramHandEditKind.AddEntity, entity));

    [McpServerTool(Name = "rename_entity", ReadOnly = false, Destructive = false)]
    [Description("Renames one entity of an erDiagram. An entity's name is its identity — every relationship is written in terms of it — so unlike rename_node this does rewrite the relationship lines that name it, and nothing else. Refused with a reason if the diagram is not an erDiagram, if there is no such entity, if the new name is already taken, or if the operator is editing either name right now.")]
    public Task<string> RenameEntity(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity to rename, as it appears in the source.")] string entity,
        [Description("The new name: one word of letters, digits or underscores.")] string renamedTo) =>
        _ApplyObjectEditAsync(session, diagram, $"rename entity {_SingleLine(entity)} to {_SingleLine(renamedTo)}", $"{entity}>{renamedTo}", [entity, renamedTo],
            new DiagramHandEdit(DiagramHandEditKind.RenameEntity, entity, Label: renamedTo));

    [McpServerTool(Name = "remove_entity", ReadOnly = false, Destructive = false)]
    [Description("Removes one entity of an erDiagram — its whole attribute block and the relationships that ran to or from it, nothing else. A relationship whose entity is gone would draw that entity again on the next render, which is why they go together; the reply says how many went with it. Refused with a reason if the diagram is not an erDiagram, if there is no such entity, or if the operator is editing it right now.")]
    public Task<string> RemoveEntity(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity to remove.")] string entity) =>
        _ApplyObjectEditAsync(session, diagram, $"remove entity {_SingleLine(entity)} and its relationships", entity, [entity],
            new DiagramHandEdit(DiagramHandEditKind.RemoveEntity, entity));

    [McpServerTool(Name = "set_attribute", ReadOnly = false, Destructive = false)]
    [Description("Writes one attribute inside an erDiagram entity's block: adds it when it is not there yet, and rewrites it when it is, so you do not have to know which. Only that one line changes; a comment already on it is kept. An entity that so far only appeared in a relationship gets its block here. Refused with a reason if the diagram is not an erDiagram, if there is no such entity, or if the operator is editing it right now.")]
    public Task<string> SetAttribute(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity whose block this attribute belongs in.")] string entity,
        [Description("The attribute's name: one word of letters, digits or underscores.")] string attribute,
        [Description("The attribute's type as it should be drawn, one word — \"string\", \"int\", \"varchar(50)\".")] string type,
        [Description("Optional key marker: PK, FK or UK. Leave it out for an attribute that is not a key.")] string? key = null) =>
        _ApplyObjectEditAsync(session, diagram, $"set attribute {_SingleLine(entity)}.{_SingleLine(attribute)}", $"{entity}.{attribute}", [entity],
            new DiagramHandEdit(DiagramHandEditKind.SetAttribute, entity) { Attribute = attribute, AttributeType = type, AttributeKey = key });

    [McpServerTool(Name = "remove_attribute", ReadOnly = false, Destructive = false)]
    [Description("Removes one attribute from an erDiagram entity's block. The entity stays, with the rest of its attributes and all of its relationships; only that one line goes. Refused with a reason if the diagram is not an erDiagram, if the entity or the attribute is not there, or if the operator is editing the entity right now.")]
    public Task<string> RemoveAttribute(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity whose block the attribute sits in.")] string entity,
        [Description("The attribute to remove.")] string attribute) =>
        _ApplyObjectEditAsync(session, diagram, $"remove attribute {_SingleLine(entity)}.{_SingleLine(attribute)}", $"{entity}.{attribute}", [entity],
            new DiagramHandEdit(DiagramHandEditKind.RemoveAttribute, entity) { Attribute = attribute });

    [McpServerTool(Name = "relate_entities", ReadOnly = false, Destructive = false)]
    [Description("Draws one relationship between two entities of an erDiagram, or rewrites the one that is already there — this is the ER counterpart of connect_nodes, and it asks for what an ER relationship cannot do without: a cardinality on each end and a label. The label is the verb the line is read by (\"places\", \"belongs to\") and is not optional here. An existing relationship keeps its solid/dashed line style. Refused with a reason if the diagram is not an erDiagram, if a cardinality or the label is missing, or if the operator is editing either entity right now.")]
    public Task<string> RelateEntities(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity the relationship reads from.")] string from,
        [Description("The entity the relationship reads to.")] string to,
        [Description("How many of `from` take part: one, zero-or-one, one-or-more or zero-or-more.")] string fromCardinality,
        [Description("How many of `to` take part: one, zero-or-one, one-or-more or zero-or-more.")] string toCardinality,
        [Description("The verb drawn on the line, read from `from` to `to` — \"places\", \"belongs to\".")] string label)
    {
        if (_Cardinality(fromCardinality) is not { } tail || _Cardinality(toCardinality) is not { } head)
        {
            return Task.FromResult(_Serialize(new { ok = false, error = "A cardinality must be one of: one, zero-or-one, one-or-more, zero-or-more." }));
        }

        return _ApplyObjectEditAsync(session, diagram, $"relate {_SingleLine(from)} -> {_SingleLine(to)}", $"{from}->{to}", [from, to, $"{from}->{to}"],
            new DiagramHandEdit(DiagramHandEditKind.Relate, from, To: to, Label: label) { FromCardinality = tail, ToCardinality = head });
    }

    [McpServerTool(Name = "unrelate_entities", ReadOnly = false, Destructive = false)]
    [Description("Removes one relationship between two entities of an erDiagram. Both entities stay, with their attributes; only the line between them goes. Refused with a reason if the diagram is not an erDiagram, if there is no such relationship, or if the operator is editing either entity (or the relationship itself) right now.")]
    public Task<string> UnrelateEntities(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The diagram to edit, by its id or name from list_diagrams.")] string diagram,
        [Description("The entity the relationship reads from.")] string from,
        [Description("The entity the relationship reads to.")] string to) =>
        _ApplyObjectEditAsync(session, diagram, $"unrelate {_SingleLine(from)} -> {_SingleLine(to)}", $"{from}->{to}", [from, to, $"{from}->{to}"],
            new DiagramHandEdit(DiagramHandEditKind.Unrelate, from, To: to));

    private static DiagramErCardinality? _Cardinality(string value) => value.Trim().ToLowerInvariant() switch
    {
        "one" => DiagramErCardinality.One,
        "zero-or-one" => DiagramErCardinality.ZeroOrOne,
        "one-or-more" => DiagramErCardinality.OneOrMore,
        "zero-or-more" => DiagramErCardinality.ZeroOrMore,
        _ => null,
    };

    private static DiagramNodeShape? _Shape(string value) => value.Trim().ToLowerInvariant() switch
    {
        "rectangle" => DiagramNodeShape.Rectangle,
        "rounded" => DiagramNodeShape.Rounded,
        "diamond" => DiagramNodeShape.Diamond,
        "stadium" => DiagramNodeShape.Stadium,
        "subroutine" => DiagramNodeShape.Subroutine,
        _ => null,
    };

    // The one path every per-object tool takes (AC-852), under the registry's lock: hold check, line surgery
    // (`registry.ComputeHandEdit`, AC-889 — the per-object grammar is internal to Infrastructure) and render all
    // see one text, nothing written unless all three pass. `objectKey` journals it for a later targeted undo (AC-853).
    private async Task<string> _ApplyObjectEditAsync(
        string session,
        string diagram,
        string ask,
        string objectKey,
        string[] objects,
        DiagramHandEdit handEdit)
    {
        if (registry.Resolve(diagram) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such diagram surface — call list_diagrams for the open surfaces and their ids." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, DiagramCapability.Edit, ask).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        string? refusal = null;
        var summary = "";
        var fidelity = new DiagramFidelity([]);
        var applied = registry.EditCoupled(caller, surface.SurfaceId, handEdit.Kind, objectKey, current =>
        {
            if (objects.FirstOrDefault(name => registry.IsHeldByOperator(surface.SurfaceId, name)) is { } held)
            {
                refusal = $"The operator is editing \"{held}\" right now, so nothing was changed. Try the same call again once they are done with it.";
                return (null, "");
            }

            var (text, editSummary, editRefusal) = registry.ComputeHandEdit(current, handEdit);
            if (editRefusal is { } reason)
            {
                refusal = reason;
                return (null, "");
            }

            if (registry.CheckFidelity(text!) is not { } checkedFidelity)
            {
                refusal = "That change would not have left valid Mermaid behind, so nothing was changed.";
                return (null, "");
            }

            fidelity = checkedFidelity;
            summary = editSummary;
            return (text, editSummary);
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
        var held = registry.CouplingOf(caller, surface.SurfaceId);
        if (needed == DiagramCapability.Read && held is { CanRead: true })
        {
            return null;
        }

        if (needed == DiagramCapability.Edit && held is { CanEdit: true })
        {
            return null;
        }

        if (held is null && registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Diagram \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        // Widening applies only to the read-then-edit path: granting Edit always grants Read alongside it, so
        // there is no "held Edit, now wants Read" case, and a fresh zero-capability coupling asking for Read is a
        // first ask, not a widening of anything.
        var widening = needed == DiagramCapability.Edit && held is { CanRead: true };

        // AC-948: the operator's own opt-out — off by default.
        if (!settings.SkipDiagramConsent)
        {
            var decision = await host.RequestConsentAsync(_PromptFor(surface, needed, widening, changeSummary)).ConfigureAwait(false);
            if (!decision.IsApproved)
            {
                return needed == DiagramCapability.Read
                    ? "Reading that diagram was not approved by the operator."
                    : "Editing that diagram was not approved by the operator — you may still be able to read it.";
            }
        }

        try
        {
            registry.Couple(caller, surface.SurfaceId);
            registry.Grant(caller, surface.SurfaceId, needed);
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
    // itself, mechanically derived (SourceChangeSummary), never text the calling agent composed.
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

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
