using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Infrastructure.Whiteboard;

// The `cockpit-whiteboard` MCP tools (AC-823), gated per-surface like `cockpit-diagram` (AC-810) — read that class
// first. Deviations: one capability, not two (no `edit_whiteboard` — AC-820's fixed boundary), and the payload is a
// base64 PNG snapshot, never the board's shapes as data — the consent text names a screenshot, not a diagram source.
internal sealed class WhiteboardMcpTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IWhiteboardAccessRegistry _registry;
    private readonly IConsentBroker? _consent;

    // The consent broker is optional so the tool's own tests construct it without a host; the container injects the
    // shared singleton, so a real access is gated behind an operator Approve/Deny that fails closed when nobody can ask.
    public WhiteboardMcpTools(IWhiteboardAccessRegistry registry, IConsentBroker? consent = null)
    {
        _registry = registry;
        _consent = consent;
    }

    [McpServerTool(Name = "list_whiteboards")]
    [Description("Lists the whiteboard surfaces the operator has open that you could ask to read: each with a stable id, the name the operator sees, and whether you already hold read on it. Reading a surface needs the operator to approve it first (see read_whiteboard); this list only names the surfaces so you can reference one.")]
    public string ListWhiteboards(
        [Description("Your session id — the value of the COCKPIT_PANE_ID environment variable in this session.")] string session)
    {
        var caller = McpRequestContext.CurrentPaneId ?? session;
        var whiteboards = _registry.ListSurfaces(caller)
            .Select(surface => new
            {
                id = surface.SurfaceId,
                name = surface.Name,
                canRead = surface.Coupling?.CanRead ?? false,
            });
        return _Serialize(new { ok = true, whiteboards });
    }

    [McpServerTool(Name = "read_whiteboard")]
    [Description("Returns a screenshot of a whiteboard surface — you name it by the id or name from list_whiteboards. This shares an IMAGE of the board exactly as it looks right now — literally what is on the operator's screen — not its shapes or strokes as data. The first time you read a surface the operator gets an Approve/Deny prompt naming which whiteboard and that a screenshot is being shared. There is no way to change a whiteboard through this tool or any other: an agent never writes to the canvas.")]
    public async Task<string> ReadWhiteboard(
        [Description("Your session id (COCKPIT_PANE_ID).")] string session,
        [Description("The whiteboard to read, by its id or name from list_whiteboards.")] string whiteboard)
    {
        if (_registry.Resolve(whiteboard) is not { } surface)
        {
            return _Serialize(new { ok = false, error = "No such whiteboard surface — call list_whiteboards for the open surfaces and their ids." });
        }

        var caller = McpRequestContext.CurrentPaneId ?? session;
        if (await _EnsureReadAsync(caller, surface).ConfigureAwait(false) is { } error)
        {
            return _Serialize(new { ok = false, error });
        }

        var snapshotPng = _registry.ReadCoupled(caller, surface.SurfaceId) ?? [];
        _registry.MarkRead(caller, surface.SurfaceId);
        return _Serialize(new
        {
            ok = true,
            id = surface.SurfaceId,
            name = surface.Name,
            mimeType = "image/png",
            imageBase64 = Convert.ToBase64String(snapshotPng),
        });
    }

    // Ensures this session holds Read on `surface`, asking the operator once. Returns an error string to surface,
    // or null when the session now holds it.
    private async Task<string?> _EnsureReadAsync(string caller, WhiteboardSurface surface)
    {
        if (_registry.CouplingOf(caller, surface.SurfaceId) is { CanRead: true })
        {
            return null;
        }

        if (_registry.IsCoupledByAnother(caller, surface.SurfaceId))
        {
            return $"Whiteboard \"{surface.Name}\" is already being used by another agent — only one agent at a time can use a surface.";
        }

        if (_consent is null)
        {
            return "Using a whiteboard surface needs the operator's approval, which is not available here.";
        }

        var decision = await _consent.RequestConsentAsync(_PromptFor(surface)).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            return "Reading that whiteboard was not approved by the operator.";
        }

        try
        {
            _registry.Couple(caller, surface.SurfaceId);
            _registry.Grant(caller, surface.SurfaceId);
        }
        catch (InvalidOperationException)
        {
            // The operator was deciding for as long as they took, and the world moved: another agent got the
            // surface first, or it closed.
            return $"Whiteboard \"{surface.Name}\" is no longer available — another agent took it, or it closed while the operator was deciding.";
        }

        return null;
    }

    // AC-823's deviation from DiagramMcpTools' read prompt: this names a screenshot being shared, not a diagram
    // source — the payload is literally what is on the operator's screen, so the consent text says that plainly.
    private static ConsentRequest _PromptFor(WhiteboardSurface surface) =>
        new(
            "An agent wants to read a whiteboard",
            $"Let this agent see a screenshot of whiteboard \"{_SingleLine(surface.Name)}\" exactly as it looks right now — this shares an image of the board, not just its shapes or text as data. It cannot draw on it: writing to a whiteboard is not offered to agents at all.",
            new ConsentSource(surface.SurfaceId, null, ConsentSourceCatalog.WhiteboardMcp),
            "whiteboard.read",
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
