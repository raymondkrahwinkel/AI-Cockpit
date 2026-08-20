using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Core.Consent;
using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Diagram;

// The `cockpit-wireframe` MCP tools (AC-872), gated per-capability like `cockpit-diagram` (AC-810) — read that
// class first. Deviations: the payload is the source text, a component is named by the stable id a read stamps on
// it (AC-906), and there is no diff gate — the journal is the safety net.
internal sealed class WireframeMcpTools(ICockpitHost host, IWireframeAccessRegistry registry, DiagramSettings settings)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private const string NoSuchSurface = "No such wireframe surface — call list_wireframes for the open surfaces and their ids.";

    [McpServerTool(Name = "list_wireframes")]
    [Description("Lists the wireframe surfaces the operator has open that you could ask to use: each with a stable id, the name the operator sees, and whether you already hold read/edit on it. Reading or editing a surface needs the operator to approve it first (see read_wireframe / edit_wireframe); this list only names the surfaces so you can reference one. A surface can be coupled to you with neither capability yet — that is a real, valid state, not an error.")]
    public string ListWireframes(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = host.CurrentMcpCallerPaneId ?? session;
        var wireframes = registry.ListSurfaces(caller)
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
        if (!parsed.HasScreens || parsed.Errors.Count > 0)
        {
            return _Serialize(new { ok = false, error = "That source is not one the wireframe format can read, so nothing was opened.", problems = _Problems(parsed) });
        }

        var title = string.IsNullOrWhiteSpace(name) ? "Wireframe" : name.Trim();
        var surfaceId = Guid.NewGuid().ToString("n");
        var caller = host.CurrentMcpCallerPaneId ?? session;

        // AC-948: the operator's own opt-out, set on this plugin's settings page — off by default.
        if (!settings.SkipWireframeConsent)
        {
            var decision = await host.RequestConsentAsync(new ConsentRequest(
                "An agent wants to open a wireframe to go through with you",
                $"Open a wireframe window \"{_SingleLine(title)}\" beside the cockpit, holding {source.Split('\n').Length} lines this agent wrote, and couple that agent to it. It cannot read the surface back or change it afterwards without asking you separately.",
                new ConsentSource(surfaceId, null, ConsentSourceCatalog.WireframeMcp),
                "wireframe.open",
                ConsentRisk.Dangerous)).ConfigureAwait(false);

            if (!decision.IsApproved)
            {
                return _Serialize(new { ok = false, error = "Opening that wireframe was not approved by the operator — nothing was opened." });
            }
        }

        Dispatcher.UIThread.Post(() =>
            _ = WireframeWindow.OpenAsync(host, new WireframeDocument(surfaceId, title, source), caller));

        return _Serialize(new { ok = true, id = surfaceId, name = title, opened = true });
    }

    [McpServerTool(Name = "read_wireframe")]
    [Description("Returns a wireframe surface's source — you name it by the id or name from list_wireframes. The first time you read a surface the operator gets an Approve/Deny prompt naming which wireframe and how big it is; only after Approve do you get its source, and it is the surface exactly as it stands now, including anything the operator put there before you connected. Reading does not let you edit — edit_wireframe asks for that separately. Alongside the raw source you get `components`: every component with the ID that add_component, set_component_text, remove_component and move_component take. An id is written in the source as `#name` and stays with its component for as long as it lives, so an id you read stays aimed at the same component even when the operator edits the screen around it — reading a surface is what gives its components ids, so the source comes back with them in it. A wireframe holds one or more screens: `screens` lists them in the order they stand in the source, and every entry in `components` says which screen it belongs to, so a component you name is never one of the same name on another screen. A component carrying `goto:` gets a `goto` field with the id of the screen it points at, so you can address that screen directly — null when the component carries no `goto:` at all, or when its title does not resolve to exactly one screen (see `problems` for why). `viewport` names the document-wide sheet size everything is measured against — desktop, tablet or mobile, with its pixel width/height — so you can judge how much room a layout actually has; a wireframe that declares none reads as desktop.")]
    public async Task<string> ReadWireframe(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to read, by its id or name from list_wireframes.")] string wireframe)
    {
        if (registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Read).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var source = registry.ReadCoupled(caller, surface.SurfaceId) ?? "";
        registry.MarkRead(caller, surface.SurfaceId);
        var parsed = WireframeParser.Parse(source);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            source,
            screens = _Screens(parsed.Screens),
            components = _Components(parsed.Screens),
            viewport = _ViewportInfo(parsed.Viewport),
            problems = _Problems(parsed),
        });
    }

    [McpServerTool(Name = "set_wireframe_viewport")]
    [Description("Sets the wireframe's viewport — the document-wide sheet size the layout is measured against — to desktop (960×640, what a wireframe that declares none already renders at), tablet (768×1024) or mobile (390×844). Applies straight away, as one undoable step from the activity strip; the components themselves do not change, only the sheet size they are judged against. Needs the same one-off Approve as edit_wireframe. Choosing the viewport already in effect does nothing. Refused with a reason if `viewport` is not one of those three names.")]
    public Task<string> SetWireframeViewport(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The viewport name: desktop, tablet or mobile.")] string viewport)
    {
        var name = viewport.Trim().ToLowerInvariant();
        if (name is not ("desktop" or "tablet" or "mobile"))
        {
            return Task.FromResult(_Serialize(new { ok = false, error = $"\"{viewport}\" is not a viewport — use desktop, tablet or mobile." }));
        }

        return _ApplyAsync(session, wireframe, WireframeComponentEdit.SetViewport(Enum.Parse<WireframeViewport>(name, ignoreCase: true)), $"set the viewport to {name}");
    }

    [McpServerTool(Name = "edit_wireframe")]
    [Description("Replaces a wireframe surface's whole source with `source` — the tool to reach for when you are writing or rewriting a screen, rather than changing one thing on one that is already there. It applies straight away; there is no accept step, so what you send is what the operator sees. Needs its own Approve, asked the first time you edit a surface (covering read too, in one prompt) or as a widening prompt if you were only reading it before. The operator's prompt shows how many lines change, computed from the actual edit — not from anything you write here. Refused if the source is not something the format can read, and the reply then says which lines are wrong. The operator can take the whole rewrite back from the activity strip, so it is one undoable step rather than an overwrite.")]
    public async Task<string> EditWireframe(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The full replacement source for this wireframe, starting with a screen line.")] string source)
    {
        if (registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var parsed = WireframeParser.Parse(source);
        if (!parsed.HasScreens || parsed.Errors.Count > 0)
        {
            return _Serialize(new { ok = false, error = "That source is not one the wireframe format can read, so nothing was changed.", problems = _Problems(parsed) });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        var changeSummary = SourceChangeSummary.Describe(registry.PeekText(surface.SurfaceId) ?? "", source);
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Edit, changeSummary).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var result = registry.WriteCoupled(caller, surface.SurfaceId, source);
        return _Reply(surface, result, extra: changeSummary);
    }

    [McpServerTool(Name = "add_component")]
    [Description("Adds ONE component inside a container and applies it straight away — every other line of the wireframe is left exactly as it is, including anything the operator changed since you last read it. `parent` is the ID of a container, from read_wireframe's `components`: screen, row, column, group, header, footer, sidebar, main, card, modal, tabs, tab, nav, menu, breadcrumb, stepper, list or table. `type` is one of those, or a widget: item, label, button, input, textarea, search, select, checkbox, radio, toggle, slider, image, avatar, icon, badge, progress, pagination, divider, space. Needs the same one-off Approve as edit_wireframe. Refused with a reason if there is no component with that id, the parent is not a container, the keyword or a modifier is not one the format has, or the operator is editing that container right now — try again once they let go.")]
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

    [McpServerTool(Name = "add_screen")]
    [Description("Adds ONE more screen to this wireframe and applies it straight away — a wireframe holds as many screens as the thing you are sketching has, and this is how you add the next one. The new screen carries only its title; fill it with add_component, naming the screen's own id from read_wireframe's `screens`. The operator sees it appear beside the others in the overview. Needs the same one-off Approve as edit_wireframe. Use remove_component to take a screen away again — the last remaining screen is refused, because a wireframe without one is nothing to look at.")]
    public Task<string> AddScreen(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The screen's title — what names it in the overview, e.g. \"Aanmelden\".")] string title,
        [Description("Where among the screens it goes, 0 for first. Omit to put it last.")] int? position = null) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.AddScreen(title, position),
            $"add a screen \"{_SingleLine(title)}\"");

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
    [Description("Removes ONE component and everything nested inside it — nothing else. `component` is the ID from read_wireframe's `components`; the reply says how many nested components went with it. A screen line removes that whole screen, unless it is the only screen this wireframe has left. Refused with a reason if there is no component with that id any more, if it is the last screen (that is the wireframe — use edit_wireframe), or if the operator is editing it right now.")]
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

    [McpServerTool(Name = "set_component_modifier")]
    [Description("Sets or clears ONE modifier on ONE component and applies it straight away, leaving everything else alone — the way to make a button primary, tick a checkbox, size a column, fill in a value or lay a flow to another screen without rebuilding the component. `modifier` is one of: primary, selected, checked, disabled, w, h, align, value, goto. For the four flags (primary/selected/checked/disabled) omit `value` to turn it on, or pass `clear: true` to take it off. For w/h (a flex ratio 1-6, never pixels), align (left/center/right), value (text for most components, 0-100 for slider/progress/pagination) and goto (a screen's title, from read_wireframe's `screens`) pass the new `value`, or `clear: true` to remove it. Refused with a reason if there is no component with that id any more, the modifier is not one this format has, it has no meaning on this component here (e.g. `w:` on something that is not a row/header/footer child), or the operator is editing it right now.")]
    public Task<string> SetComponentModifier(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the component to change.")] string component,
        [Description("The modifier keyword: primary, selected, checked, disabled, w, h, align, value or goto.")] string modifier,
        [Description("The value to set, e.g. `2` for w/h, `right` for align, `Raymond` for value, a screen's title for goto. Leave empty for a flag modifier you are turning on.")] string? value = null,
        [Description("True to remove the modifier instead of setting it.")] bool clear = false)
    {
        if (!Enum.TryParse<WireframeModifierName>(modifier.Trim(), ignoreCase: true, out var name))
        {
            return Task.FromResult(_Serialize(new { ok = false, error = $"\"{modifier}\" is not a modifier this format has — use one of: primary, selected, checked, disabled, w, h, align, value, goto." }));
        }

        var isFlag = name is WireframeModifierName.Primary or WireframeModifierName.Selected or WireframeModifierName.Checked or WireframeModifierName.Disabled;
        // AC-902: a screen title carries spaces almost by default, so goto: is quoted unconditionally — the
        // int.TryParse check that spares value: a pair of quotes would read "Wachtwoord vergeten" as two tokens.
        var quoted = name == WireframeModifierName.Goto || (name == WireframeModifierName.Value && !int.TryParse(value, out _));
        var edit = isFlag
            ? WireframeComponentEdit.ToggleModifier(component, name, on: !clear)
            : WireframeComponentEdit.SetModifier(component, name, clear ? null : value, quoted: quoted);
        var ask = clear
            ? $"clear {_Keyword(name)} on component #{_SingleLine(component)}"
            : $"set {_Keyword(name)} on component #{_SingleLine(component)}{(isFlag ? "" : $" to \"{_SingleLine(value ?? "")}\"")}";
        return _ApplyAsync(session, wireframe, edit, ask);
    }

    [McpServerTool(Name = "change_component_type")]
    [Description("Changes what ONE component is — label into a button, input into a select — keeping its place, its text, its modifiers and its id; only the keyword changes. Applies straight away. `type` is any keyword the format has: screen, row, column, group, header, footer, sidebar, main, card, modal, tabs, tab, nav, menu, breadcrumb, stepper, list, table, item, label, button, input, textarea, search, select, checkbox, radio, toggle, slider, image, avatar, icon, badge, progress, pagination, divider, space. Refused with a reason if there is no component with that id any more, it is the screen line itself, the keyword is not one the format has, the new type cannot carry children and this component still has some (move or remove them first), or the operator is editing it right now.")]
    public Task<string> ChangeComponentType(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The wireframe to edit, by its id or name from list_wireframes.")] string wireframe,
        [Description("The id of the component to retype.")] string component,
        [Description("The new component keyword, e.g. button, select, group.")] string type) =>
        _ApplyAsync(session, wireframe, WireframeComponentEdit.ChangeType(component, type),
            $"change component #{_SingleLine(component)} into a {type.Trim().ToLowerInvariant()}");

    // The one path every per-component tool takes (AC-852's shape). Same Edit consent as edit_wireframe, then the
    // edit runs inside the registry's lock, where the hold check, the line surgery and the "does this still parse"
    // gate all see one source and nothing is written unless all three pass.
    private async Task<string> _ApplyAsync(string session, string wireframe, WireframeComponentEdit edit, string ask)
    {
        if (registry.Resolve(wireframe) is not { } surface)
        {
            return _Serialize(new { ok = false, error = NoSuchSurface });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WireframeCapability.Edit, ask).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        return _Reply(surface, registry.EditCoupled(caller, surface.SurfaceId, edit));
    }

    // Every write answers the same way: what changed, plus the components as they now stand — the ids are the same
    // ones, so this is a fresh picture of the screen rather than a new set of handles.
    private string _Reply(WireframeSurface surface, WireframeEditResult result, string? extra = null)
    {
        if (result.Refusal is { } refusal)
        {
            return _Serialize(new { ok = false, error = refusal });
        }

        var parsed = WireframeParser.Parse(registry.PeekText(surface.SurfaceId) ?? "");
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            changed = result.Summary,
            changeSummary = extra,
            screens = _Screens(parsed.Screens),
            components = _Components(parsed.Screens),
        });
    }

    // Ensures this session holds at least `needed` on `surface`, asking the operator once for exactly that much.
    // Returns an error string to surface, or null when the session now holds it. `ask` describes the change about to
    // happen and is only meaningful (and only supplied) for an Edit ask.
    private async Task<string?> _EnsureCapabilityAsync(string caller, WireframeSurface surface, WireframeCapability needed, string? ask = null)
    {
        var held = registry.CouplingOf(caller, surface.SurfaceId);
        if (needed == WireframeCapability.Read ? held is { CanRead: true } : held is { CanEdit: true })
        {
            return null;
        }

        if (held is null && registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Wireframe \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        // Widening applies only to the read-then-edit path: granting Edit always grants Read alongside it, so there
        // is no "held Edit, now wants Read" case.
        var widening = needed == WireframeCapability.Edit && held is { CanRead: true };

        // AC-948: the operator's own opt-out — off by default.
        if (!settings.SkipWireframeConsent)
        {
            var decision = await host.RequestConsentAsync(_PromptFor(surface, needed, widening, ask)).ConfigureAwait(false);
            if (!decision.IsApproved)
            {
                return needed == WireframeCapability.Read
                    ? "Reading that wireframe was not approved by the operator."
                    : "Editing that wireframe was not approved by the operator — you may still be able to read it.";
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
    // take, `line` is there for pointing at a problem, `depth` says what sits inside what, and `screen` (AC-901)
    // says which of the document's screens it belongs to — the same id the screen itself is listed under.
    private static IReadOnlyList<object> _Components(IReadOnlyList<WireframeNode> screens)
    {
        var components = new List<object>();
        foreach (var screen in screens)
        {
            _Collect(screen, screen, screens, 0, components);
        }

        return components;
    }

    // The screens of the document, in the order they stand in the source — what an overview shows, and what an
    // agent picks from before it starts naming components.
    private static IReadOnlyList<object> _Screens(IReadOnlyList<WireframeNode> screens) =>
        screens.Select(screen => (object)new { id = screen.Id, line = screen.Line, title = screen.Text }).ToList();

    private static void _Collect(WireframeNode node, WireframeNode screen, IReadOnlyList<WireframeNode> screens, int depth, List<object> into)
    {
        // AC-902 AC1: the id of the screen a goto: resolves to, not its raw title text — so the agent can address
        // the target with the other tools directly, without redoing the title lookup itself. Null when the
        // component carries no goto: or the title does not resolve (see `problems` for why).
        var target = node.ValueOf(WireframeModifierName.Goto) is { } title
            ? WireframeGotoResolver.Resolve(screens, title).Screen?.Id
            : null;

        into.Add(new
        {
            id = node.Id,
            line = node.Line,
            depth,
            screen = screen.Id,
            screenTitle = screen.Text,
            type = node.Kind.ToString().ToLowerInvariant(),
            text = node.Text,
            isContainer = node.IsContainer,
            @goto = target,
        });

        foreach (var child in node.Children)
        {
            _Collect(child, screen, screens, depth + 1, into);
        }
    }

    private static object _ViewportInfo(WireframeViewport? parsed)
    {
        var viewport = parsed ?? WireframeViewport.Desktop;
        var size = WireframeRenderer.SizeOf(viewport);
        return new { name = viewport.ToString().ToLowerInvariant(), width = size.Width, height = size.Height };
    }

    private static IReadOnlyList<object> _Problems(WireframeParseResult parsed) =>
        parsed.Errors.Select(error => (object)new { line = error.Line, problem = error.Message }).ToList();

    private static string _Quoted(string? text) => string.IsNullOrEmpty(text) ? "" : $" \"{_SingleLine(text)}\"";

    private static string _Keyword(WireframeModifierName name) => name.ToString().ToLowerInvariant();

    // Fold anything a consent surface could render as a line break out of the wireframe name and the described
    // change before they go verbatim into the Dangerous prompt (cf. DiagramMcpTools, AC-80/AC-92).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
