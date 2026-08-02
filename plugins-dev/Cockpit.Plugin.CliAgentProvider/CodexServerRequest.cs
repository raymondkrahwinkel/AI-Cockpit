using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

// A server-to-client JSON-RPC request from `codex app-server` (a message with both an `id` and a
// `method`) — an approval the server is blocking on (`item/commandExecution/requestApproval`,
// `item/fileChange/requestApproval`, …). The driver must answer it with
// `CodexAppServerConnection.RespondAsync` echoing `Id`, or the turn stalls.
//
// `Id`: The request id, cloned verbatim (number or string) so it can be echoed back in the response.
// `Params`: The request's `params`, cloned; `JsonValueKind.Undefined` when it carried none.
internal sealed record CodexServerRequest(JsonElement Id, string Method, JsonElement Params);
