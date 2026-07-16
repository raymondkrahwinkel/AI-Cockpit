using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// A server-to-client JSON-RPC notification from <c>codex app-server</c> (a message with a <c>method</c> and no
/// <c>id</c>) — the streaming transcript surface (<c>item/*</c>, <c>turn/*</c>, <c>thread/started</c>, …). The
/// driver reads these off <see cref="CodexAppServerConnection.Notifications"/> and maps them to plugin events.
/// </summary>
/// <param name="Params">
/// The notification's <c>params</c>, cloned so it outlives the parsed document; <see cref="JsonValueKind.Undefined"/>
/// when the notification carried none.
/// </param>
internal sealed record CodexNotification(string Method, JsonElement Params);
