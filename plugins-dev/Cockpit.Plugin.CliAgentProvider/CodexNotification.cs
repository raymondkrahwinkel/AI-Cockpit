using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// A server-to-client JSON-RPC notification from `codex app-server` (a message with a `method` and no
// `id`) — the streaming transcript surface (`item/*`, `turn/*`, `thread/started`, …). The
// driver reads these off `CodexAppServerConnection.Notifications` and maps them to plugin events.
//
// `Params`:
// The notification's `params`, cloned so it outlives the parsed document; `JsonValueKind.Undefined`
// when the notification carried none.
internal sealed record CodexNotification(string Method, JsonElement Params);
