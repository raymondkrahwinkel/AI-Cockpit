using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Infrastructure.Plugins;

// AC-1389: every plugin's channel between its backend and UI part, keyed by plugin id so one plugin never reaches
// another's actions or events (that is what intents are for). Events are numbered on the backend's one counter
// (F1.4), so a remote stream (F5) resumes over plugin events and session events alike.
public sealed class PluginChannelHub : ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PluginId, string Action), Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly Dictionary<(string PluginId, string Name), List<Action<PluginChannelEvent>>> _subscribers = [];

    public IPluginBackendChannel For(string pluginId) => new BackendChannel(this, pluginId);

    public void Handle(string pluginId, string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryAdd((pluginId, action), handler))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' already registered a channel handler for action '{action}'. Each action has one handler.");
            }
        }
    }

    public Task<JsonElement> InvokeAsync(string pluginId, string action, JsonElement payload, CancellationToken cancellationToken)
    {
        Func<JsonElement, CancellationToken, Task<JsonElement>>? handler;
        lock (_gate)
        {
            _handlers.TryGetValue((pluginId, action), out handler);
        }

        return handler is null
            ? Task.FromException<JsonElement>(new PluginChannelUnknownActionException(pluginId, action))
            : handler(payload, cancellationToken);
    }

    public void Publish(string pluginId, string name, JsonElement payload)
    {
        // Delivered outside the gate, so a subscriber may publish or subscribe itself. Two publishers racing can
        // deliver seq 6 before 5; the seq is what lets a subscriber tell.
        PluginChannelEvent channelEvent;
        Action<PluginChannelEvent>[] subscribers;
        lock (_gate)
        {
            channelEvent = new PluginChannelEvent(name, SessionEventSequence.Next(), payload);
            subscribers = _subscribers.TryGetValue((pluginId, name), out var list) ? [.. list] : [];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber(channelEvent);
        }
    }

    public IDisposable Subscribe(string pluginId, string name, Action<PluginChannelEvent> handler)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue((pluginId, name), out var list))
            {
                list = [];
                _subscribers[(pluginId, name)] = list;
            }

            list.Add(handler);
        }

        return new Subscription(this, (pluginId, name), handler);
    }

    private void _Unsubscribe((string PluginId, string Name) key, Action<PluginChannelEvent> handler)
    {
        lock (_gate)
        {
            if (_subscribers.TryGetValue(key, out var list))
            {
                list.Remove(handler);
            }
        }
    }

    private sealed class BackendChannel(PluginChannelHub hub, string pluginId) : IPluginBackendChannel
    {
        public void Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) =>
            hub.Handle(pluginId, action, handler);

        public void Publish(string name, JsonElement payload) => hub.Publish(pluginId, name, payload);
    }

    private sealed class Subscription(PluginChannelHub hub, (string PluginId, string Name) key, Action<PluginChannelEvent> handler) : IDisposable
    {
        public void Dispose() => hub._Unsubscribe(key, handler);
    }
}
