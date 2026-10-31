using System.Text.Json;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-1511: a plugin's channel to its backend part on a server, for the view it offers in Admin → Plugins there.
internal sealed class RemotePluginUiChannel(IPluginChannelRelay relay, string folderId) : IPluginUiChannel
{
    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        relay.InvokeChannelAsync(folderId, action, payload, cancellationToken);

    // AC-1547: the plugin's events on the server, over the server's event stream; an admin key's only, as the channel is.
    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) =>
        relay.SubscribeChannel(folderId, name, (seq, payload) => handler(new PluginChannelEvent(name, seq, payload)));
}
