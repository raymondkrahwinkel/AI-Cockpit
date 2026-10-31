using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Infrastructure.Plugins;

// AC-1389: every plugin's channel between its backend and UI part, keyed by plugin id so one plugin never reaches
// another's actions or events (that is what intents are for). Events are numbered on the backend's one counter
// (F1.4), so a remote stream (F5) resumes over plugin events and session events alike.
public sealed class PluginChannelHub(ILogger<PluginChannelHub> logger) : ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PluginId, string Action), Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = [];
    private readonly Dictionary<(string PluginId, string Name), List<Action<PluginChannelEvent>>> _subscribers = [];

    // AC-1547: every event of every plugin, after its own subscribers, so the event log can carry it to a remote desktop.
    public event Action<string, PluginChannelEvent>? Published;

    public IPluginBackendChannel For(string pluginId) => new BackendChannel(this, pluginId);

    public IDisposable Handle(string pluginId, string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryAdd((pluginId, action), handler))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginId}' already registered a channel handler for action '{action}'. Each action has one handler.");
            }
        }

        return new Registration(() => _Unhandle((pluginId, action), handler));
    }

    public Task<JsonElement> InvokeAsync(string pluginId, string action, JsonElement payload, CancellationToken cancellationToken)
    {
        Func<JsonElement, CancellationToken, Task<JsonElement>>? handler;
        lock (_gate)
        {
            _handlers.TryGetValue((pluginId, action), out handler);
        }

        // AC-1891: never inline. A UI control calling in would otherwise run the backend's I/O on the UI thread
        // up to the handler's first real await, and that await would capture the UI context.
        if (handler is null)
        {
            return Task.FromException<JsonElement>(new PluginChannelUnknownActionException(pluginId, action));
        }

        var owned = _Own(payload);
        return Task.Run(() => handler(owned, cancellationToken), cancellationToken);
    }

    public void Publish(string pluginId, string name, JsonElement payload)
    {
        // Delivered outside the gate, so a subscriber may publish or subscribe itself. Two publishers racing can
        // deliver seq 6 before 5; the seq is what lets a subscriber tell.
        PluginChannelEvent channelEvent;
        Action<PluginChannelEvent>[] subscribers;
        lock (_gate)
        {
            channelEvent = new PluginChannelEvent(name, SessionEventSequence.Next(), _Own(payload));
            subscribers = _subscribers.TryGetValue((pluginId, name), out var list) ? [.. list] : [];
        }

        // A throwing subscriber stays that plugin's problem: logged, and the rest still get the event.
        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(channelEvent);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A subscriber of plugin {PluginId} threw on channel event {EventName}; the other subscribers still get it.", pluginId, name);
            }
        }

        try
        {
            Published?.Invoke(pluginId, channelEvent);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Plugin {PluginId}'s channel event {EventName} could not be put on the event stream.", pluginId, name);
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

        return new Registration(() => _Unsubscribe((pluginId, name), handler));
    }

    // A copy the receiver may keep past an await, after the sender disposed the document it came from, as a
    // remote receiver always can. `default` (no payload) has no document to copy.
    private static JsonElement _Own(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Undefined ? payload : payload.Clone();

    // Removes the handler only while it is still the registered one, so a stale handle cannot drop its successor.
    private void _Unhandle((string PluginId, string Action) key, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        lock (_gate)
        {
            if (_handlers.TryGetValue(key, out var current) && current == handler)
            {
                _handlers.Remove(key);
            }
        }
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
        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) =>
            hub.Handle(pluginId, action, handler);

        public void Publish(string name, JsonElement payload) => hub.Publish(pluginId, name, payload);
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        public void Dispose() => remove();
    }
}
