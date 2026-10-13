using System.Text.Json;

namespace Cockpit.Core.Abstractions.Plugins;

/// <summary>
/// Reaches a plugin's backend part on a server through its own channel (AC-1511), for the view that plugin offers an
/// admin of that server. Invokes only; the server's events do not travel this way.
/// </summary>
public interface IPluginChannelRelay
{
    Task<JsonElement> InvokeChannelAsync(string folderId, string action, JsonElement payload, CancellationToken cancellationToken = default);
}
