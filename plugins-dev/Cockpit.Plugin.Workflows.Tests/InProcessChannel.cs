using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Workflows.Tests;

// Both halves of the plugin's channel in one object (AC-1399, copied from the GitHubIssues tests): what the backend part
// handles is what the UI part invokes, as the host's PluginChannelHub does for one plugin. This plugin publishes
// (RunRecorded), so events are kept for a test to read and handed to the subscribers.
internal sealed class InProcessChannel : IPluginBackendChannel, IPluginUiChannel
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly List<(string Name, Action<PluginChannelEvent> Handler)> _subscribers = [];

    public List<PluginChannelEvent> Published { get; } = [];

    public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _handlers.Add(action, handler);
        return new Registration(() => _handlers.Remove(action));
    }

    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        _handlers.TryGetValue(action, out var handler)
            ? handler(payload, cancellationToken)
            : throw new PluginChannelUnknownActionException("workflows", action);

    public void Publish(string name, JsonElement payload)
    {
        var channelEvent = new PluginChannelEvent(name, Published.Count + 1, payload);
        Published.Add(channelEvent);
        foreach (var subscriber in _subscribers.Where(subscriber => subscriber.Name == name).ToList())
        {
            subscriber.Handler(channelEvent);
        }
    }

    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler)
    {
        var subscription = (name, handler);
        _subscribers.Add(subscription);
        return new Registration(() => _subscribers.Remove(subscription));
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
