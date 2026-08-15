using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// An agent-to-client JSON-RPC notification from `opencode acp` (a message with a `method` and no `id`) —
// on the wire this is almost always `session/update`, whose `params.update.sessionUpdate` discriminates the
// streaming variants (agent_message_chunk, tool_call, usage_update, …) — see OpencodeSessionUpdateMapper.
// The driver reads these off `OpencodeAcpConnection.Notifications` and maps them to plugin events.
//
// `Params`: The notification's `params`, cloned so it outlives the parsed document; `JsonValueKind.Undefined`
// when the notification carried none.
internal sealed record OpencodeNotification(string Method, JsonElement Params);
