using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// A server-to-client JSON-RPC request from <c>codex app-server</c> (a message with both an <c>id</c> and a
/// <c>method</c>) — an approval the server is blocking on (<c>item/commandExecution/requestApproval</c>,
/// <c>item/fileChange/requestApproval</c>, …). The driver must answer it with
/// <see cref="CodexAppServerConnection.RespondAsync"/> echoing <see cref="Id"/>, or the turn stalls.
/// </summary>
/// <param name="Id">The request id, cloned verbatim (number or string) so it can be echoed back in the response.</param>
/// <param name="Params">The request's <c>params</c>, cloned; <see cref="JsonValueKind.Undefined"/> when it carried none.</param>
internal sealed record CodexServerRequest(JsonElement Id, string Method, JsonElement Params);
