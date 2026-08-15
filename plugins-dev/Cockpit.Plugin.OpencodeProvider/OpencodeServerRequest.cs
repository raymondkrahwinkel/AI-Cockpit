using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// An agent-to-client JSON-RPC request (an `id` and a `method`) — the blocking session/request_permission
// approval, or an unmodelled kind. `Id` may start at 0, not 1 — measured live, never assume otherwise.
internal sealed record OpencodeServerRequest(JsonElement Id, string Method, JsonElement Params);
