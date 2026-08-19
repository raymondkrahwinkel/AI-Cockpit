using System.ComponentModel;
using System.Text.Json;
using Avalonia.Threading;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Consent;
using Cockpit.Plugin.Diagram.Whiteboard;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Diagram;

// The `cockpit-whiteboard` MCP tools (AC-823), gated per-capability like `cockpit-diagram` (AC-810) — read that
// class first. Deviations: the read payload is a base64 PNG snapshot, so the consent text names a screenshot; and
// the write path (AC-854, reversing AC-820) only adds — no replace-the-board tool, no reach into operator work.
internal sealed class WhiteboardMcpTools(ICockpitHost host, IWhiteboardAccessRegistry registry)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // PlacedShapeKind's own names (the whiteboard plugin parses them case-insensitively), minus Image — a picture on
    // the board is the operator's paste, not something an agent hands over.
    private static readonly string[] Shapes =
        ["rectangle", "roundedrectangle", "ellipse", "diamond", "arrow", "column", "callout", "text", "stickynote"];

    [McpServerTool(Name = "list_whiteboards")]
    [Description("Lists the whiteboard surfaces the operator has open that you could ask to use: each with a stable id, the name the operator sees, and whether you already hold read/place on it. Reading a surface and putting something on it each need the operator to approve them first, separately (see read_whiteboard / place_on_whiteboard); this list only names the surfaces so you can reference one.")]
    public string ListWhiteboards(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = host.CurrentMcpCallerPaneId ?? session;
        var whiteboards = registry.ListSurfaces(caller)
            .Select(surface => new
            {
                id = surface.SurfaceId,
                name = surface.Name,
                canRead = surface.Coupling?.CanRead ?? false,
                canPlace = surface.Coupling?.CanWrite ?? false,
            });
        return _Serialize(new { ok = true, whiteboards });
    }

    [McpServerTool(Name = "open_whiteboard")]
    [Description("Asks the operator to put a fresh whiteboard on their screen, so the two of you can work something out on it together — this is how you get a board when none is open, rather than waiting for the operator to make one. The operator gets an Approve/Deny prompt naming the board; on Approve a whiteboard window opens beside the cockpit, empty, coupled to you. On Deny nothing opens at all. The coupling on its own grants nothing: seeing the board (read_whiteboard) and putting anything on it (place_on_whiteboard) still ask their own approval.")]
    public async Task<string> OpenWhiteboard(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The name the operator sees on the window — say what the board is for.")] string name)
    {
        var title = string.IsNullOrWhiteSpace(name) ? "Whiteboard" : name.Trim();
        var surfaceId = Guid.NewGuid().ToString("n");
        var caller = host.CurrentMcpCallerPaneId ?? session;

        var decision = await host.RequestConsentAsync(new ConsentRequest(
            "An agent wants to open a whiteboard to work on with you",
            $"Open an empty whiteboard window \"{_SingleLine(title)}\" beside the cockpit and couple this agent to it. It cannot see the board or draw on it without asking you separately.",
            new ConsentSource(surfaceId, null, ConsentSourceCatalog.WhiteboardMcp),
            "whiteboard.open",
            ConsentRisk.Dangerous)).ConfigureAwait(false);

        if (!decision.IsApproved)
        {
            return _Serialize(new { ok = false, error = "Opening that whiteboard was not approved by the operator — nothing was opened." });
        }

        Dispatcher.UIThread.Post(() =>
            _ = WhiteboardWindow.OpenAsync(host, new WhiteboardDocument(surfaceId, title), caller));

        return _Serialize(new { ok = true, id = surfaceId, name = title, opened = true });
    }

    [McpServerTool(Name = "read_whiteboard")]
    [Description("Returns a screenshot of a whiteboard surface — you name it by the id or name from list_whiteboards. This shares an IMAGE of the WHOLE board, scaled to fit — a render of what is drawn and placed on it, not a crop of whatever the operator happens to have in view, and not its shapes or strokes as data. The first time you read a surface the operator gets an Approve/Deny prompt naming which whiteboard and that a screenshot is being shared. Reading does not let you put anything on the board — place_on_whiteboard asks for that separately.")]
    public async Task<string> ReadWhiteboard(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The whiteboard to read, by its id or name from list_whiteboards.")] string whiteboard)
    {
        if (registry.Resolve(whiteboard) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such whiteboard surface — call list_whiteboards for the open surfaces and their ids." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WhiteboardCapability.Read).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var snapshotPng = registry.ReadCoupled(caller, surface.SurfaceId) ?? [];
        registry.MarkRead(caller, surface.SurfaceId);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            mimeType = "image/png",
            imageBase64 = Convert.ToBase64String(snapshotPng),
        });
    }

    [McpServerTool(Name = "place_on_whiteboard")]
    [Description("Puts ONE object on a whiteboard surface — a shape, a sticky note or a bare label — and leaves everything else on the board exactly as it is. There is no way to replace a board or to move, change or remove anything the operator drew or placed themselves; you only add. What you put down is drawn in the agent's crisp blue and badged as yours, so the operator can always see which marks are theirs and which are yours. Needs its own Approve, asked the first time you place something on a surface (covering reading it too, in one prompt) or as a widening prompt if you were only reading it before. Returns the new object's id, which erase_whiteboard_object takes to take it back.")]
    public async Task<string> PlaceOnWhiteboard(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The whiteboard to put something on, by its id or name from list_whiteboards.")] string whiteboard,
        [Description("The shape to place: rectangle, roundedrectangle, ellipse, diamond, arrow, column, callout, text (a bare label) or stickynote.")] string shape,
        [Description("The text drawn in it; leave empty for an unlabelled shape.")] string? label = null,
        [Description("Left edge in board pixels, from the top-left corner (the board is 2400x1800 — the operator can pan/zoom to reach any of it, so this is not limited to what they currently have in view).")] double x = 40,
        [Description("Top edge in board pixels, from the top-left corner.")] double y = 40,
        [Description("Width in board pixels; omit for a sensible default.")] double width = 0,
        [Description("Height in board pixels; omit for a sensible default.")] double height = 0)
    {
        if (registry.Resolve(whiteboard) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such whiteboard surface — call list_whiteboards for the open surfaces and their ids." });
        }

        var kind = (shape ?? "").Replace(" ", "").Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (!Shapes.Contains(kind))
        {
            return _Serialize(new { ok = false, error = $"\"{shape}\" is not a shape this board has — use one of: {string.Join(", ", Shapes)}." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        var ask = string.IsNullOrWhiteSpace(label) ? $"a {kind}" : $"a {kind} reading \"{_SingleLine(label!)}\"";
        if (await _EnsureCapabilityAsync(caller, surface, WhiteboardCapability.Write, ask).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var sticky = kind == "stickynote";
        var placement = new WhiteboardPlacement(
            kind,
            string.IsNullOrWhiteSpace(label) ? null : label,
            x,
            y,
            width > 0 ? width : sticky ? 140 : 120,
            height > 0 ? height : sticky ? 140 : 80);

        if (registry.PlaceCoupled(caller, surface.SurfaceId, placement) is not { } objectId)
        {
            return _Serialize(new { ok = false, error = "That whiteboard could not be written to — it may have closed or been disconnected." });
        }

        return _Serialize(new { ok = true, id = surface.SurfaceId, name = surface.Name, objectId, placed = ask });
    }

    [McpServerTool(Name = "erase_whiteboard_object")]
    [Description("Takes back one object YOU placed on a whiteboard, by the objectId place_on_whiteboard returned. It only reaches your own objects: anything the operator drew or placed themselves is refused, not removed — their board is theirs. Uses the same approval as place_on_whiteboard.")]
    public async Task<string> EraseWhiteboardObject(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The whiteboard, by its id or name from list_whiteboards.")] string whiteboard,
        [Description("The id place_on_whiteboard gave the object.")] string objectId)
    {
        if (registry.Resolve(whiteboard) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such whiteboard surface — call list_whiteboards for the open surfaces and their ids." });
        }

        var caller = host.CurrentMcpCallerPaneId ?? session;
        if (await _EnsureCapabilityAsync(caller, surface, WhiteboardCapability.Write, "take back an object it placed itself").ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        if (!registry.ErasePlaced(caller, surface.SurfaceId, objectId))
        {
            return _Serialize(new
            {
                ok = false,
                error = "That object is not one you placed, so it was left alone — an agent only takes back its own objects, never the operator's work.",
            });
        }

        return _Serialize(new { ok = true, id = surface.SurfaceId, name = surface.Name, objectId, erased = true });
    }

    // Ensures this session holds at least `needed` on `surface`, asking the operator once for exactly that much.
    // Returns an error string to surface, or null when the session now holds it. `ask` describes the write about to
    // happen and is only meaningful (and only supplied) for a Write ask.
    private async Task<string?> _EnsureCapabilityAsync(string caller, WhiteboardSurface surface, WhiteboardCapability needed, string? ask = null)
    {
        var held = registry.CouplingOf(caller, surface.SurfaceId);
        if (needed == WhiteboardCapability.Read ? held is { CanRead: true } : held is { CanWrite: true })
        {
            return null;
        }

        if (held is null && registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Whiteboard \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        // A session that already holds Read gets the widening prompt: the operator approved reading under AC-820's
        // promise that an agent never writes to the canvas, so drawing is a new question, not an extension of that one.
        var widening = needed == WhiteboardCapability.Write && held is { CanRead: true };
        var decision = await host.RequestConsentAsync(_PromptFor(surface, needed, widening, ask)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return needed == WhiteboardCapability.Read
                ? "Reading that whiteboard was not approved by the operator."
                : "Putting something on that whiteboard was not approved by the operator — you may still be able to read it.";
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
            return $"Whiteboard \"{surface.Name}\" is no longer available — another agent took it, or it closed while the operator was deciding.";
        }

        return null;
    }

    // Read names a screenshot being shared, not a diagram source (AC-823's deviation) — a render of the whole
    // board (AC-913), never a crop of whatever the operator has scrolled into view. Write's prompt (AC-854) says
    // what is about to be put down, built from the shape/label the call carries, not from the agent's own prose.
    private static ConsentRequest _PromptFor(WhiteboardSurface surface, WhiteboardCapability needed, bool widening, string? ask) =>
        needed == WhiteboardCapability.Read
            ? new ConsentRequest(
                "An agent wants to read a whiteboard",
                $"Let this agent see a screenshot of the whole whiteboard \"{_SingleLine(surface.Name)}\", scaled to fit — not just the part of it the operator currently has in view — this shares an image of the board, not just its shapes or text as data. It cannot put anything on the board: that is a separate question, asked separately.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.WhiteboardMcp),
                "whiteboard.read",
                ConsentRisk.Dangerous)
            : new ConsentRequest(
                widening
                    ? "An agent that is reading a whiteboard now wants to draw on it too"
                    : "An agent wants to read a whiteboard and draw on it",
                $"Let this agent put objects on whiteboard \"{_SingleLine(surface.Name)}\" (starting with {ask}). It adds one object at a time, drawn in its own blue and badged as the agent's, next to your own marks — it cannot move, change or remove anything you drew or placed, and it cannot replace the board. You can rub out anything it puts there, and Disconnect at any time.",
                new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.WhiteboardMcp),
                "whiteboard.write",
                ConsentRisk.Dangerous);

    // Fold anything a consent surface could render as a line break out of the whiteboard name before it goes
    // verbatim into the Dangerous prompt (cf. DiagramMcpTools, TerminalMcpTools).
    private static string _SingleLine(string value) =>
        new(value.Select(character =>
            char.IsControl(character) || character == 0x2028 || character == 0x2029 || character == 0x0085
                ? ' '
                : character).ToArray());

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, SerializerOptions);
}
