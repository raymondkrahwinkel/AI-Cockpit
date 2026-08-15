using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// An agent-to-client JSON-RPC notification (a `method`, no `id`) — almost always `session/update`, whose
// `sessionUpdate` field discriminates the streaming variants; see OpencodeSessionUpdateMapper.
internal sealed record OpencodeNotification(string Method, JsonElement Params);
