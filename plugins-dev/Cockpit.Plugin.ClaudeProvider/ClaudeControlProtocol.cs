using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The <c>claude</c> stream-json <em>control protocol</em> (Fase 4, SDK route) — the in-band, stdio-only permission
/// channel that lets the cockpit answer tool-approval prompts over the same pipes the transcript streams on, with no
/// external <c>--permission-prompt-tool</c> MCP server. This is Claude's equivalent of Codex's in-band
/// <c>item/*/requestApproval</c> round-trip (see <c>CodexAppServerSessionDriver</c>): the CLI, running in bidirectional
/// stream-json mode without a permission-prompt tool, sends a <c>can_use_tool</c> control_request whenever a tool needs
/// approval, and the client answers with a control_response over stdin.
/// </summary>
/// <remarks>
/// Every wire shape here is taken verbatim from the official Agent SDK's own transport
/// (<c>claude-agent-sdk-python/src/claude_agent_sdk/_internal/query.py</c>, <c>Query._handle_control_request</c>),
/// which implements exactly this round-trip — not reconstructed from memory:
/// <list type="bullet">
/// <item>Inbound (CLI → cockpit, one stdout line):
///   <c>{"type":"control_request","request_id":"…","request":{"subtype":"can_use_tool","tool_name":"…","input":{…},"tool_use_id":"…"}}</c>.
///   <c>tool_use_id</c> is optional (<c>permission_request.get("tool_use_id")</c>); it correlates the prompt to the
///   <c>tool_use</c> block already seen in the transcript, the way Codex correlates on <c>itemId</c>.</item>
/// <item>Outbound allow (cockpit → CLI, one stdin line):
///   <c>{"type":"control_response","response":{"subtype":"success","request_id":"…","response":{"behavior":"allow","updatedInput":{…}}}}</c>.
///   <c>updatedInput</c> defaults to the request's original <c>input</c> when unchanged.</item>
/// <item>Outbound deny: the same envelope with <c>response:{"behavior":"deny","message":"…"}</c> — a <em>deny</em> is
///   still a <em>success</em> callback (subtype "success"); "error" is reserved for a callback that threw.</item>
/// <item>Startup: an <c>initialize</c> control_request (<c>{"subtype":"initialize","hooks":null}</c>) puts an SDK client
///   on the control channel so the CLI routes permission prompts here rather than to its interactive/MCP path.</item>
/// </list>
/// F-C1 caveat mirrors the rest of this plugin: no logged-in <c>claude</c> CLI exists in this sandbox, so the live end
/// of this round-trip (the CLI actually emitting <c>can_use_tool</c> for this spawn shape) is Raymond's eyeball item.
/// The parse/build round-trip below is fully unit-tested; if a field name ever drifts, it changes in this one file.
/// </remarks>
internal static class ClaudeControlProtocol
{
    /// <summary>Envelope discriminator values the CLI can put on a control line.</summary>
    public const string ControlRequestType = "control_request";
    public const string ControlResponseType = "control_response";
    public const string ControlCancelType = "control_cancel_request";

    private const string _CanUseToolSubtype = "can_use_tool";

    /// <summary>
    /// True when <paramref name="type"/> names a control-protocol line (a reply to one of our own requests, or an
    /// inbound request from the CLI) rather than a transcript event — so the driver routes it here instead of to
    /// <see cref="ClaudeStreamJson"/>.
    /// </summary>
    public static bool IsControlLine(string? type) =>
        type is ControlRequestType or ControlResponseType or ControlCancelType;

    /// <summary>
    /// The <c>initialize</c> control_request line sent once at startup. Carries a fresh <paramref name="requestId"/> so
    /// a correlated reply could be matched (the driver only logs it, per F-C1 scope). <c>hooks:null</c> mirrors the SDK
    /// when no hooks are registered.
    /// </summary>
    public static string BuildInitializeRequest(string requestId) =>
        JsonSerializer.Serialize(new
        {
            type = ControlRequestType,
            request_id = requestId,
            request = new { subtype = "initialize", hooks = (object?)null },
        });

    /// <summary>
    /// Recognises an inbound <c>can_use_tool</c> control_request and extracts the fields the cockpit needs to surface a
    /// prompt. Returns false for any other control line (initialize replies, cancels, hook callbacks we do not model),
    /// leaving the driver to log-and-ignore it. <paramref name="toolUseId"/> falls back to <paramref name="requestId"/>
    /// when the request omits its own — the response echoes <paramref name="requestId"/> either way, so the fallback only
    /// affects which transcript card the prompt attaches to, never the CLI round-trip.
    /// </summary>
    public static bool TryParsePermissionRequest(
        JsonElement root,
        out string requestId,
        out string toolUseId,
        out string toolName,
        out string inputJson)
    {
        requestId = string.Empty;
        toolUseId = string.Empty;
        toolName = string.Empty;
        inputJson = "{}";

        if (!_TryGetString(root, "type", out var type) || type != ControlRequestType
            || !root.TryGetProperty("request", out var request) || request.ValueKind != JsonValueKind.Object
            || !_TryGetString(request, "subtype", out var subtype) || subtype != _CanUseToolSubtype
            || !_TryGetString(root, "request_id", out var id))
        {
            return false;
        }

        requestId = id;
        toolName = _TryGetString(request, "tool_name", out var name) ? name : string.Empty;
        inputJson = request.TryGetProperty("input", out var input) && input.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? input.GetRawText()
            : "{}";
        toolUseId = _TryGetString(request, "tool_use_id", out var tuid) && tuid.Length > 0 ? tuid : id;
        return true;
    }

    /// <summary>
    /// The <c>control_response</c> line answering the permission request keyed by <paramref name="requestId"/>. An allow
    /// carries the original <paramref name="originalInputJson"/> back as <c>updatedInput</c> (the cockpit never rewrites
    /// tool input); a deny carries the operator's <paramref name="message"/>. Both are <c>subtype:"success"</c> — the
    /// callback succeeded and returned a decision; only a thrown callback would be "error".
    /// </summary>
    public static string BuildDecisionResponse(string requestId, bool allow, string originalInputJson, string denyMessage)
    {
        object decision = allow
            ? new { behavior = "allow", updatedInput = _ParseOrEmptyObject(originalInputJson) }
            : new { behavior = "deny", message = denyMessage };

        return JsonSerializer.Serialize(new
        {
            type = ControlResponseType,
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = decision,
            },
        });
    }

    // The original input rides back verbatim on allow. It arrives as a raw JSON string; re-parse it into a node the
    // serializer emits as an object rather than a re-escaped string. A blank/garbled input degrades to {} rather than
    // failing the whole response — the tool still runs, just without an echoed input the CLI already has.
    private static JsonElement _ParseOrEmptyObject(string inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return _EmptyObject();
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return _EmptyObject();
        }
    }

    private static JsonElement _EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static bool _TryGetString(JsonElement parent, string property, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
