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

    // ponytail: invokes only; a server view that needs the backend's events gets them over the event stream then.
    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
