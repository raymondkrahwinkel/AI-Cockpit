using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cockpit.Infrastructure.Events;

// AC-1547: every plugin's channel events onto the backend event log, so a plugin's own screen on a desktop that manages
// this backend follows them live (`RemotePluginUiChannel.Subscribe`). `EventsEndpoint` gives them to admin keys only.
internal sealed class PluginEventsBridge(PluginChannelHub hub, IBackendEventLog log, IServiceProvider services) : IHostedService, ISingletonService
{
    public const string EventKind = "plugin";

    private static readonly JsonElement NoPayload = JsonDocument.Parse("null").RootElement.Clone();

    private int _listeners;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        hub.Published += _OnPublished;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        hub.Published -= _OnPublished;
        return Task.CompletedTask;
    }

    // Held by each open admin stream: only then do plugin events go into the log, so a plugin's chatty events (a CI
    // run's output lines) never push session rows out of its ring or flood the local readers nobody remote watches.
    public IDisposable Listen()
    {
        Interlocked.Increment(ref _listeners);
        return new Listener(this);
    }

    // Named by its folder, as a desktop addresses the plugin's channel route; a plugin not loaded from a folder has no reader.
    private void _OnPublished(string pluginId, PluginChannelEvent channelEvent)
    {
        if (Volatile.Read(ref _listeners) == 0)
        {
            return;
        }

        var folderId = services.GetService<PluginManager>()?.LoadedParts
            .Select(part => part.Discovered)
            .FirstOrDefault(discovered => string.Equals(discovered.Manifest.Id, pluginId, StringComparison.Ordinal))?.FolderId;
        if (folderId is null)
        {
            return;
        }

        var payload = channelEvent.Payload.ValueKind == JsonValueKind.Undefined ? NoPayload : channelEvent.Payload;
        log.Append(EventKind, null, new { folder = folderId, name = channelEvent.Name, payload });
    }

    private sealed class Listener(PluginEventsBridge bridge) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref bridge._listeners);
            }
        }
    }
}

// The `data` of a `plugin` event on the stream, as a desktop reads it.
public sealed record PluginEventFrame(string Folder, string Name, JsonElement Payload);
