using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Consent;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Infrastructure.Collab;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Wireframe;

// The `cockpit-wireframe` MCP tools (AC-872), gated per-capability like `cockpit-diagram` (AC-810) — read that
// class first. Deviations: the payload is the source text, a component is named by the stable id a read stamps on
// it (AC-906), and there is no diff gate — the journal is the safety net.
internal sealed class WireframeMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private const string NoSuchSurface = "No such wireframe surface — call list_wireframes for the open surfaces and their ids.";

    private readonly IWireframeAccessRegistry _registry;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real access is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public WireframeMcpTools(IWireframeAccessRegistry registry, IConsentBroker? consent = null)
    {
        _registry = registry;
        _consent = consent;
    }

    [McpServerTool(Name = "list_wireframes")]
    [Description("Lists the wireframe surfaces the operator has open that you could ask to use: each with a stable id, the name the operator sees, and whether you already hold read/edit on it. Reading or editing a surface needs the operator to approve it first (see read_wireframe / edit_wireframe); this list only names the surfaces so you can reference one. A surface can be coupled to you with neither capability yet — that is a real, valid state, not an error.")]
    public string ListWireframes(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var wireframes = _registry.ListSurfaces(caller)
            .Select(surface => new
            {
                id = surface.SurfaceId,
                name = surface.Name,
                canRead = surface.Coupling?.CanRead ?? false,
                canEdit = surface.Coupling?.CanEdit ?? false,
            });
        return _Serialize(new { ok = true, wireframes });
    }

    [McpServerTool(Name = "open_wireframe")]
    [Description("Asks the operator to put a wireframe YOU wrote on their screen, so the two of you can go through it together — this is how you show a screen sketch nobody has open yet. The wireframe format is plain text: one component per line, nesting by indentation, no coordinates and no colours (see docs/wireframe-format.md). The operator gets an Approve/Deny prompt naming the wireframe and how big it is; on Approve a wireframe window opens beside the cockpit with your source in it, coupled to you. On Deny nothing opens at all. The coupling on its own grants nothing: reading the surface back, or editing it afterwards, still ask their own approval. Refused without asking if the source is not something the format can read.")]
    public async Task<string> OpenWireframe(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The name the operator sees on the window — say which screen this sketches.")] string name,
        [Description("The wireframe source to open it with, starting with a screen line.")] string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return _Serialize(new { ok = false, error = "Give the source of the wireframe you want to go through — an empty screen is nothing to discuss." });
        }

        var parsed = WireframeParser.Parse(source);
        if (parsed.Root is null || parsed.Errors.Count > 0)
        {
            return _Serialize(new { ok = false, error = "That source is not one the wireframe format can read, so nothing was opened.", problems = _Problems(parsed) });
        }

        var title = string.IsNullOrWhiteSpace(name) ? "Wireframe" : name.Trim();
        var surfaceId = Guid.NewGuid().ToString("n");
        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (_consent is null)
        {
            return _Serialize(new { ok = false, error = "Opening a wireframe needs the operator's approval, which is not available here." });
        }

        var decision = await _consent.RequestConsentAsync(new ConsentRequest(
            "An agent wants to open a wireframe to go through with you",
            $"Open a wireframe window \"{_SingleLine(title)}\" beside the cockpit, holding {source.Split('\n').Length} lines this agent wrote, and couple that agent to it. It cannot read the surface back or change it afterwards without asking you separately.",
            new ConsentSource(surfaceId, null, ConsentSourceCatalog.WireframeMcp),
            "wireframe.open",
            ConsentRisk.Dangerous)).ConfigureAwait(false);

        if (!decision.IsApproved)
        {
            return _Serialize(new { ok = false, error = "Opening that wireframe was not approved by the operator — nothing was opened." });
        }

        if (!_registry.RequestOpen(new WireframeOpenRequest(surfaceId, title, source, caller)))
        {
            return _Serialize(new { ok = false, error = "Nothing in this cockpit draws wireframe windows right now — the diagram plugin may not be running." });
        }

        return _Serialize(new { ok = true, id = surfaceId, name = title, opened = true });
    }

    [McpServerTool(Name = "read_wireframe")]
    [Description("Returns a wireframe surface's source — you name it by the id or name from list_wireframes. The first time you read a surface the operator gets an Approve/Deny prompt naming which wireframe and how big it is; only after Approve do you get its source, and it is the surface exactly as it stands now, including anything the operator put there before you connected. Reading does not let you edit — edit_wireframe asks for that separately. Alongside the raw source you get `components`: every component with the ID that add_component, set_component_text, remove_component and move_component take. An id is written in the source as `#name` and stays with its component for as long as it lives, so an id you read stays aimed at the same component even when the operator edits the screen around it — reading a surface is what gives its components ids, so the source comes back with them in it.")]
    public async Task<string> ReadWireframe(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to read, by its id or name from list_wireframes.")] string wireframe)
    {
        if (_registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Read).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var source = _registry.ReadCoupled(caller, surface.SurfaceId) ?? "";
        _registry.MarkRead(caller, surface.SurfaceId);
        var parsed = WireframeParser.Parse(source);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            source,
            components = _Components(parsed.Root),
            problems = _Problems(parsed),
        });
    }

    [McpServerTool(Name = "edit_wireframe")]
    [Description("Replaces a wireframe surface's whole source with `source` — the tool to reach for when you are writing or rewriting a screen, rather than changing one thing on one that is already there. It applies straight away; there is no accept step, so what you send is what the operator sees. Needs its own Approve, asked the first time you edit a surface (covering read too, in one prompt) or as a widening prompt if you were only reading it before. The operator's prompt shows how many lines change, computed from the actual edit — not from anything you write here. Refused if the source is not something the format can read, and the reply then says which lines are wrong. The operator can take the whole rewrite back from the activity strip, so it is one undoable step rather than an overwrite.")]
    public async Task<string> EditWireframe(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The full replacement source for this wireframe, starting with a screen line.")] string source)
    {
        if (_registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var parsed = WireframeParser.Parse(source);
        if (parsed.Root is null || parsed.Errors.Count > 0)
        {
            return _Serialize(new { ok = false, error = "That source is not one the wireframe format can read, so nothing was changed.", problems = _Problems(parsed) });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        var changeSummary = SourceChangeSummary.Describe(_registry.PeekText(surface.SurfaceId) ?? "", source);
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Edit, changeSummary).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var result = _registry.WriteCoupled(caller, surface.SurfaceId, source);
        return _Reply(surface, result, extra: changeSummary);
    }

    [McpServerTool(Name = "add_component")]
    [Description("Adds ONE component inside a container and applies it straight away — every other line of the wireframe is left exactly as it is, including anything the operator changed since you last read it. `parent` is the ID of the container it goes into (a screen, row, column, group, tabs, tab, nav, list or table), from read_wireframe's `components`. `type` is a keyword such as row, column, group, label, button, input, select, checkbox, radio, item, image, divider or space. Needs the same one-off Approve as edit_wireframe. Refused with a reason if there is no component with that id, the parent is not a container, the keyword or a modifier is not one the format has, or the operator is editing that container right now — try again once they let go.")]
    public Task<string> AddComponent(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the container this goes into.")] string parent,
        [Description("The component keyword, e.g. button, input, group, row.")] string type,
        [Description("The component's text — a button's caption, a field's label. Leave empty for one that carries none.")] string? text = null,
        [Description("Modifiers exactly as the source spells them, space-separated, e.g. `primary`, `w:2 align:right`, `value:\"Raymond\"`.")] string? modifiers = null,
        [Description("Where among the container's existing children it goes, 0 for first. Omit to put it last.")] int? position = null) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.Add(parent, type, text, modifiers, position),
            $"add {type.Trim().ToLowerInvariant()}{_Quoted(text)}");

    [McpServerTool(Name = "set_component_text")]
    [Description("Changes ONE component's text — a button's caption, a field's label, a screen's title — and applies it straight away, leaving every other line alone. The component keeps all of its modifiers and its id. `component` is the ID from read_wireframe's `components`. Refused with a reason if there is no component with that id any more, or if the operator is editing it right now.")]
    public Task<string> SetComponentText(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the component to reword.")] string component,
        [Description("The new text.")] string text) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.SetText(component, text),
            $"reword component #{_SingleLine(component)} to \"{_SingleLine(text)}\"");

    [McpServerTool(Name = "remove_component")]
    [Description("Removes ONE component and everything nested inside it — nothing else. `component` is the ID from read_wireframe's `components`; the reply says how many nested components went with it. Refused with a reason if there is no component with that id any more, if it is the screen line itself (that is the wireframe — use edit_wireframe), or if the operator is editing it right now.")]
    public Task<string> RemoveComponent(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the component to remove.")] string component) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.Remove(component),
            $"remove component #{_SingleLine(component)} and anything inside it");

    [McpServerTool(Name = "move_component")]
    [Description("Moves ONE component, with everything nested inside it, into another container — the way to reorder a row's buttons or lift a field into a different group without rewriting the screen. Both `component` and `parent` are IDs from read_wireframe's `components`; the block is re-indented to fit where it lands. Refused with a reason if either id names no component any more, if the target is not a container or is inside the component itself, or if the operator is editing either of them right now.")]
    public Task<string> MoveComponent(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the component to move.")] string component,
        [Description("The id of the container it moves into.")] string parent,
        [Description("Where among that container's children it lands, 0 for first. Omit to put it last.")] int? position = null) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.Move(component, parent, position),
            $"move component #{_SingleLine(component)} into container #{_SingleLine(parent)}");

    // The one path every per-component tool takes (AC-852's shape). Same Edit consent as edit_wireframe, then the
    // edit runs inside the registry's lock, where the hold check, the line surgery and the "does this still parse"
    // gate all see one source and nothing is written unless all three pass.
    private async Task<string> _ApplyAsync(string session, string wireframe, WireframeComponentEdit edit, string ask)
    {
        if (_registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Edit, ask).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        return _Reply(surface, _registry.EditCoupled(caller, surface.SurfaceId, edit));
    }

    // Every write answers the same way: what changed, plus the components as they now stand — the ids are the same
    // ones, so this is a fresh picture of the screen rather than a new set of handles.
    private string _Reply(WireframeSurface surface, WireframeEditResult result, string? extra = null)
    {
        if (result.Refusal is { } refusal)
        {
            return _Serialize(new { ok = false, error = refusal });
        }

        var parsed = WireframeParser.Parse(_registry.PeekText(surface.SurfaceId) ?? "");
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            changed = result.Summary,
            changeSummary = extra,
            components = _Components(parsed.Root),
        });
    }

    // Ensures this session holds at least `needed` on `surface`, asking the operator once for exactly that much.
    // Returns an error string to surface, or null when the session now holds it. `ask` describes the change about to
    // happen and is only meaningful (and only supplied) for an Edit ask.
    private async Task<string?> _EnsureCapabilityAsync(string caller, WireframeSurface surface, WireframeCapability needed, string? ask = null)
    {
        var held = _registry.CouplingOf(caller, surface.SurfaceId);
        if (needed == WireframeCapability.Read ? held is { CanRead: true } : held is { CanEdit: true })
        {
            return null;
        }

        if (held is null && _registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Wireframe \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        if (_consent is null)
        {
            return "Using a wireframe surface needs the operator's approval, which is not available here.";
        }

        // Widening applies only to the read-then-edit path: granting Edit always grants Read alongside it, so there
        // is no "held Edit, now wants Read" case.
        var widening = needed == WireframeCapability.Edit && held is { CanRead: true };
        var decision = await _consent.RequestConsentAsync(_PromptFor(surface, needed, widening, ask)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return needed == WireframeCapability.Read
                ? "Reading that wireframe was not approved by the operator."
                : "Editing that wireframe was not approved by the operator — you may still be able to read it.";
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
            return $"Wireframe \"{surface.Name}\" is no longer available — another agent took it, or it closed while the operator was deciding.";
        }

        return null;
    }

    // Read's prompt says the wireframe text is being shared, and that reading stamps ids — the one thing a read
    // does write (AC-906). Edit's prompt states the change itself, derived from the call's own arguments or from
    // the real line diff, never from prose the calling agent composed (AC-489).
    private static ConsentRequest _PromptFor(WireframeSurface surface, WireframeCapability needed, bool widening, string? ask) =>
        needed == WireframeCapability.Read
            ? new ConsentRequest(
                "An agent wants to read a wireframe",
                $"Let this agent read the wireframe text of screen \"{_SingleLine(surface.Name)}\" exactly as it stands now — including everything already in it. Reading marks each component with a short id such as #c1, so a later change names the component rather than a line that has since moved; nothing else about the screen changes, and changing it is a separate question, asked separately.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.WireframeMcp),
                "wireframe.read",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                widening
                    ? "An agent that is reading a wireframe now wants to edit it"
                    : "An agent wants to read a wireframe and edit it",
                $"Let this agent edit wireframe \"{_SingleLine(surface.Name)}\" ({ask}). Every change lands straight away — there is no accept step — but each one is a line in the activity strip that you can take back on its own. You can watch, edit alongside, and Disconnect at any time.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.WireframeMcp),
                "wireframe.edit",
                ConsentRisk.Dangerous);

    // The flat component list every read and every write hands back: the id is the handle the per-component tools
    // take, `line` is there for pointing at a problem, and `depth` says what sits inside what.
    private static IReadOnlyList<object> _Components(WireframeNode? root)
    {
        var components = new List<object>();
        _Collect(root, 0, components);
        return components;
    }

    private static void _Collect(WireframeNode? node, int depth, List<object> into)
    {
        if (node is null)
        {
            return;
        }

        into.Add(new
        {
            id = node.Id,
            line = node.Line,
            depth,
            type = node.Kind.ToString().ToLowerInvariant(),
            text = node.Text,
            isContainer = node.IsContainer,
        });

        foreach (var child in node.Children)
        {
            _Collect(child, depth + 1, into);
        }
    }

    private static IReadOnlyList<object> _Problems(WireframeParseResult parsed) =>
        parsed.Errors.Select(error => (object)new { line = error.Line, problem = error.Message }).ToList();

    private static string _Quoted(string? text) => string.IsNullOrEmpty(text) ? "" : $" \"{_SingleLine(text)}\"";

    // Fold anything a consent surface could render as a line break out of the wireframe name and the described
    // change before they go verbatim into the Dangerous prompt (cf. DiagramMcpTools, AC-80/AC-92).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
