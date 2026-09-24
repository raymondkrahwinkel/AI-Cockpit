using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugins.Abstractions.UI;

// AC-1389: the UI half of a plugin's own channel; the backend half is ICockpitHost.Channel. JSON only, so the
// same calls work when the UI is in another process than the backend (F5/F6).
public interface IPluginUiChannel
{
    /// <summary>
    /// Invokes <paramref name="action"/> on this plugin's backend part and returns what its handler answered.
    /// Throws <see cref="PluginChannelUnknownActionException"/> when the backend registered no such action —
    /// another plugin's actions included, which this channel never reaches.
    /// </summary>
    Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to the events this plugin's backend publishes under
    /// <paramref name="name"/>, until the returned handle is disposed. The handler runs on the publishing thread,
    /// not the UI thread: marshal before touching a control.
    /// </summary>
    IDisposable Subscribe(string name, Action<PluginChannelEvent> handler);
}
