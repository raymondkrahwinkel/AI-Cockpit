using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

// An agent-to-client JSON-RPC notification from `kimi acp` (a message with a `method` and no
// `id`) — on the wire this is almost always `session/update`, whose `params.update.sessionUpdate`
// discriminates the seven streaming variants (agent_message_chunk, tool_call, plan, …). The driver reads these
// off `KimiAcpConnection.Notifications` and maps them to plugin events.
//
// `Params`:
// The notification's `params`, cloned so it outlives the parsed document; `JsonValueKind.Undefined`
// when the notification carried none.
internal sealed record KimiNotification(string Method, JsonElement Params);
