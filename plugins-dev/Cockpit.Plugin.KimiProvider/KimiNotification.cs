using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// An agent-to-client JSON-RPC notification from <c>kimi acp</c> (a message with a <c>method</c> and no
/// <c>id</c>) — on the wire this is almost always <c>session/update</c>, whose <c>params.update.sessionUpdate</c>
/// discriminates the seven streaming variants (agent_message_chunk, tool_call, plan, …). The driver reads these
/// off <see cref="KimiAcpConnection.Notifications"/> and maps them to plugin events.
/// </summary>
/// <param name="Params">
/// The notification's <c>params</c>, cloned so it outlives the parsed document; <see cref="JsonValueKind.Undefined"/>
/// when the notification carried none.
/// </param>
internal sealed record KimiNotification(string Method, JsonElement Params);
