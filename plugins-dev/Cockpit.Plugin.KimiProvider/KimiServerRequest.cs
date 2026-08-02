using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

// An agent-to-client JSON-RPC request from `kimi acp` (a message with both an `id` and a
// `method`) — the blocking `session/request_permission` approval, or an unmodelled kind such as
// `fs/read_text_file`/`fs/write_text_file` (never sent, since this driver advertises
// `clientCapabilities.fs.*` as `false`). The driver must answer it with
// `KimiAcpConnection.RespondAsync` echoing `Id`, or the agent stalls.
//
// `Id`: The request id, cloned verbatim (number or string) so it can be echoed back in the response.
// `Params`: The request's `params`, cloned; `JsonValueKind.Undefined` when it carried none.
internal sealed record KimiServerRequest(JsonElement Id, string Method, JsonElement Params);
