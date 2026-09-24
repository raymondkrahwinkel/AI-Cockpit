using System.Text.Json;

namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// The backend half of a plugin's own channel (AC-1389): the actions its UI part may invoke and the events it
/// publishes to that UI part. A plugin only ever reaches its own channel — another plugin's actions do not exist
/// for it — and nothing crosses but JSON, so the same calls work in-process and over a remote transport.
/// </summary>
public interface IPluginBackendChannel
{
    /// <summary>
    /// Registers <paramref name="handler"/> for <paramref name="action"/>: what answers when this plugin's UI part
    /// invokes it, until the returned handle is disposed — which a plugin does when it is disabled or reloaded.
    /// Registering an action that is still registered throws, so which handler runs is never a question of order.
    /// </summary>
    IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler);

    /// <summary>
    /// Publishes an event named <paramref name="name"/> to this plugin's UI subscribers, on the calling thread. It
    /// carries the backend's one sequence number, which rises with every event the backend publishes. A subscriber
    /// that throws is logged and skipped; the others still receive the event, and the publisher never sees it.
    /// </summary>
    void Publish(string name, JsonElement payload);
}

/// <summary>
/// One event a plugin's backend published on its channel, as its UI part receives it.
/// </summary>
/// <param name="Name">The name the backend published it under.</param>
/// <param name="Seq">The backend-wide sequence number: rising, shared with every other backend event, never reused.</param>
/// <param name="Payload">What the backend published, as JSON.</param>
public sealed record PluginChannelEvent(string Name, long Seq, JsonElement Payload);

/// <summary>
/// Thrown to a plugin's UI part that invoked an action its own backend never registered — including one only
/// another plugin handles, which is deliberately indistinguishable from one nobody handles.
/// </summary>
public sealed class PluginChannelUnknownActionException(string pluginId, string action)
    : InvalidOperationException($"Unknown action '{action}' on the channel of plugin '{pluginId}'.")
{
    /// <summary>
    /// The plugin whose channel was asked.
    /// </summary>
    public string PluginId { get; } = pluginId;

    /// <summary>
    /// The action it has no handler for.
    /// </summary>
    public string Action { get; } = action;
}

// What ICockpitHost.Channel hands a host that predates the channel, such as a plugin's test double: it keeps
// no handler and publishes to nobody.
internal sealed class NullPluginBackendChannel : IPluginBackendChannel
{
    public static readonly NullPluginBackendChannel Instance = new();

    private NullPluginBackendChannel()
    {
    }

    public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) =>
        NoRegistration.Instance;

    public void Publish(string name, JsonElement payload)
    {
    }

    private sealed class NoRegistration : IDisposable
    {
        public static readonly NoRegistration Instance = new();

        public void Dispose()
        {
        }
    }
}
