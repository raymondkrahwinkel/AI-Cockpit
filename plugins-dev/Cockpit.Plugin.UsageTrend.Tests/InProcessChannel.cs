using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.UsageTrend.Tests;

// Both halves of the plugin's channel in one object (AC-1390's pattern, mirrors GitStatus.Tests'
// InProcessChannel): what the backend part handles is what the UI part invokes, as the host's PluginChannelHub
// does for one plugin. UsageTrend publishes nothing, so events go nowhere.
internal sealed class InProcessChannel : IPluginBackendChannel, IPluginUiChannel
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];

    public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _handlers.Add(action, handler);
        return new Registration(() => _handlers.Remove(action));
    }

    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        _handlers.TryGetValue(action, out var handler)
            ? handler(payload, cancellationToken)
            : throw new PluginChannelUnknownActionException("usage-trend", action);

    public void Publish(string name, JsonElement payload)
    {
    }

    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Registration(() => { });

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
