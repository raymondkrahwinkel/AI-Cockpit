using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubPullRequests.Tests;

// Both halves of the plugin's channel in one object (AC-1396), as the host's PluginChannelHub does for one plugin:
// what the backend part handles is what the UI part invokes, and what it publishes reaches the UI subscribers on
// the publishing thread. Every publish is also kept, so a backend test can read what it sent with nobody listening.
internal sealed class InProcessChannel : IPluginBackendChannel, IPluginUiChannel
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly List<(string Name, Action<PluginChannelEvent> Handler)> _subscribers = [];
    private long _seq;

    public List<PluginChannelEvent> Published { get; } = [];

    public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        _handlers.Add(action, handler);
        return new Registration(() => _handlers.Remove(action));
    }

    public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
        _handlers.TryGetValue(action, out var handler)
            ? handler(payload, cancellationToken)
            : throw new PluginChannelUnknownActionException("github-pull-requests", action);

    public void Publish(string name, JsonElement payload)
    {
        var channelEvent = new PluginChannelEvent(name, Interlocked.Increment(ref _seq), payload);
        lock (Published)
        {
            Published.Add(channelEvent);
        }

        foreach (var (_, handler) in _subscribers.Where(subscriber => subscriber.Name == name).ToList())
        {
            handler(channelEvent);
        }
    }

    public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler)
    {
        var subscriber = (name, handler);
        _subscribers.Add(subscriber);
        return new Registration(() => _subscribers.Remove(subscriber));
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
