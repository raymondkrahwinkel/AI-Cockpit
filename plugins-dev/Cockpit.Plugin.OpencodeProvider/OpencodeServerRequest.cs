using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// An agent-to-client JSON-RPC request from `opencode acp` (a message with both an `id` and a `method`) —
// the blocking `session/request_permission` approval, or an unmodelled kind such as
// `fs/read_text_file`/`fs/write_text_file` (never sent, since this driver advertises
// `clientCapabilities.fs.*` as `false`, measured live: opencode honoured that and never sent one in this
// session's probing).
//
// `Id`: The request id, cloned verbatim (number or string) so it can be echoed back in the response —
// measured live: opencode's own server-request ids start at 0, not 1, so a caller must not assume ids begin
// at any particular number.
// `Params`: The request's `params`, cloned; `JsonValueKind.Undefined` when it carried none.
internal sealed record OpencodeServerRequest(JsonElement Id, string Method, JsonElement Params);
