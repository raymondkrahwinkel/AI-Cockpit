using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// An agent-to-client JSON-RPC request from <c>kimi acp</c> (a message with both an <c>id</c> and a
/// <c>method</c>) — the blocking <c>session/request_permission</c> approval, or an unmodelled kind such as
/// <c>fs/read_text_file</c>/<c>fs/write_text_file</c> (never sent, since this driver advertises
/// <c>clientCapabilities.fs.*</c> as <see langword="false"/>). The driver must answer it with
/// <see cref="KimiAcpConnection.RespondAsync"/> echoing <see cref="Id"/>, or the agent stalls.
/// </summary>
/// <param name="Id">The request id, cloned verbatim (number or string) so it can be echoed back in the response.</param>
/// <param name="Params">The request's <c>params</c>, cloned; <see cref="JsonValueKind.Undefined"/> when it carried none.</param>
internal sealed record KimiServerRequest(JsonElement Id, string Method, JsonElement Params);
