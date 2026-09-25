using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.YouTrack.Tests;

// Both halves of the plugin's channel in one object (AC-1397), as the host's PluginChannelHub joins them for one
// plugin: what the backend part handles is what the UI part invokes, and what it publishes reaches the UI's
// subscribers — synchronously, which the hub does not promise but no test here depends on either way.
internal sealed class InProcessChannel : IPluginBackendChannel, IPluginUiChannel
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly List<(string Name, Action<PluginChannelEvent> Handler)> _subscribers = [];
    private long _seq;

    public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _handlers.Add(action, handler);
        return new Registration(() => _handlers.Remove(action));
    }

    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        _handlers.TryGetValue(action, out var handler)
            ? handler(payload, cancellationToken)
            : throw new PluginChannelUnknownActionException("youtrack", action);

    public void Publish(string name, JsonElement payload)
    {
        var channelEvent = new PluginChannelEvent(name, ++_seq, payload);
        foreach (var subscriber in _subscribers.Where(subscriber => subscriber.Name == name).ToList())
        {
            subscriber.Handler(channelEvent);
        }
    }

    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler)
    {
        var entry = (name, handler);
        _subscribers.Add(entry);
        return new Registration(() => _subscribers.Remove(entry));
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
