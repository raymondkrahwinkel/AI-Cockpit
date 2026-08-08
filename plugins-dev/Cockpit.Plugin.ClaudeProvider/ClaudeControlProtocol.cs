using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider;

// The `claude` stream-json *control protocol* (Fase 4, SDK route) — the in-band, stdio-only permission
// channel that lets the cockpit answer tool-approval prompts over the same pipes the transcript streams on, with no
// external `--permission-prompt-tool` MCP server. This is Claude's equivalent of Codex's in-band
// `item/*/requestApproval` round-trip (see `CodexAppServerSessionDriver`): the CLI, running in bidirectional
// stream-json mode without a permission-prompt tool, sends a `can_use_tool` control_request whenever a tool needs
// approval, and the client answers with a control_response over stdin.
// Every wire shape here is taken verbatim from the official Agent SDK's own transport
// (`claude-agent-sdk-python/src/claude_agent_sdk/_internal/query.py`, `Query._handle_control_request`),
// which implements exactly this round-trip — not reconstructed from memory:
// - Inbound (CLI → cockpit, one stdout line):
//   `{"type":"control_request","request_id":"…","request":{"subtype":"can_use_tool","tool_name":"…","input":{…},"tool_use_id":"…"}}`.
//   `tool_use_id` is optional (`permission_request.get("tool_use_id")`); it correlates the prompt to the
//   `tool_use` block already seen in the transcript, the way Codex correlates on `itemId`.
// - Outbound allow (cockpit → CLI, one stdin line):
//   `{"type":"control_response","response":{"subtype":"success","request_id":"…","response":{"behavior":"allow","updatedInput":{…}}}}`.
//   `updatedInput` defaults to the request's original `input` when unchanged.
// - Outbound deny: the same envelope with `response:{"behavior":"deny","message":"…"}` — a *deny* is
//   still a *success* callback (subtype "success"); "error" is reserved for a callback that threw.
// - Startup: an `initialize` control_request (`{"subtype":"initialize","hooks":null}`) puts an SDK client
//   on the control channel so the CLI routes permission prompts here rather than to its interactive/MCP path.
// F-C1 caveat mirrors the rest of this plugin: no logged-in `claude` CLI exists in this sandbox, so the live end
// of this round-trip (the CLI actually emitting `can_use_tool` for this spawn shape) needs a manual eyeball check.
// The parse/build round-trip below is fully unit-tested; if a field name ever drifts, it changes in this one file.
internal static class ClaudeControlProtocol
{
    // Envelope discriminator values the CLI can put on a control line.
    public const string ControlRequestType = "control_request";
    public const string ControlResponseType = "control_response";
    public const string ControlCancelType = "control_cancel_request";

    private const string _CanUseToolSubtype = "can_use_tool";

    // True when `type` names a control-protocol line (a reply to one of our own requests, or an
    // inbound request from the CLI) rather than a transcript event — so the driver routes it here instead of to
    // `ClaudeStreamJson`.
    public static bool IsControlLine(string? type) =>
        type is ControlRequestType or ControlResponseType or ControlCancelType;

    // The `initialize` control_request line sent once at startup. Carries a fresh `requestId` so
    // a correlated reply could be matched (the driver only logs it, per F-C1 scope). `hooks:null` mirrors the SDK
    // when no hooks are registered.
    public static string BuildInitializeRequest(string requestId) =>
        BuildRequest(requestId, new { subtype = "initialize", hooks = (object?)null });

    // Any control_request line, carrying the `requestId` the CLI echoes back on its reply. The
    // fire-and-forget requests (set_model, set_permission_mode) ignore that reply; the ones this driver waits for
    // (`get_usage`, `get_context_usage`) correlate on it — see `TryParseResponse`.
    public static string BuildRequest(string requestId, object request) =>
        JsonSerializer.Serialize(new
        {
            type = ControlRequestType,
            request_id = requestId,
            request,
        });

    // Recognises a `control_response` — the CLI's reply to one of *our* requests — and hands back the
    // `request_id` it answers together with its payload. Wire shape, verbatim from a live 2.1.226 session:
    // `{"type":"control_response","response":{"subtype":"success","request_id":"…","response":{…}}}`, and
    // `{"…":{"subtype":"error","request_id":"…","error":"…"}}` when the CLI refuses. An error yields
    // <see langword="false"/> with the id still set, so the caller can release its awaiter rather than let it
    // hang for a reply that has already come and gone.
    // The payload is cloned because the caller's `JsonDocument` is disposed the moment the line is handled, and
    // this element outlives it on whichever thread was waiting.
    public static bool TryParseResponse(JsonElement root, out string requestId, out JsonElement payload)
    {
        requestId = string.Empty;
        payload = default;

        if (!_TryGetString(root, "type", out var type) || type != ControlResponseType
            || !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !_TryGetString(response, "request_id", out var id))
        {
            return false;
        }

        requestId = id;
        if (!_TryGetString(response, "subtype", out var subtype) || subtype != "success"
            || !response.TryGetProperty("response", out var inner) || inner.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        payload = inner.Clone();
        return true;
    }

    // Recognises an inbound `can_use_tool` control_request and extracts the fields the cockpit needs to surface a
    // prompt. Returns false for any other control line (initialize replies, cancels, hook callbacks we do not model),
    // leaving the driver to log-and-ignore it. `toolUseId` falls back to `requestId`
    // when the request omits its own — the response echoes `requestId` either way, so the fallback only
    // affects which transcript card the prompt attaches to, never the CLI round-trip.
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

    // The `control_response` line answering the permission request keyed by `requestId`. An allow
    // carries the original `originalInputJson` back as `updatedInput` (the cockpit never rewrites
    // tool input); a deny carries the operator's `message`. Both are `subtype:"success"` — the
    // callback succeeded and returned a decision; only a thrown callback would be "error".
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
