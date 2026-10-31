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

    private readonly Lock _gate = new();
    private readonly Dictionary<(string PluginId, string Name), string> _last = [];

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

    // ponytail: an event equal to the last one of its plugin and name is skipped, so a status a plugin repeats every tick
    // does not push session events out of the log's ring; a reader that needs the state reads it from the plugin.
    // Named by its folder, as a desktop addresses the plugin's channel route; a plugin not loaded from a folder has no reader.
    private void _OnPublished(string pluginId, PluginChannelEvent channelEvent)
    {
        var folderId = services.GetService<PluginManager>()?.LoadedParts
            .Select(part => part.Discovered)
            .FirstOrDefault(discovered => string.Equals(discovered.Manifest.Id, pluginId, StringComparison.Ordinal))?.FolderId;
        if (folderId is null)
        {
            return;
        }

        var raw = channelEvent.Payload.ValueKind == JsonValueKind.Undefined ? "null" : channelEvent.Payload.GetRawText();
        lock (_gate)
        {
            if (_last.TryGetValue((pluginId, channelEvent.Name), out var last) && last == raw)
            {
                return;
            }

            _last[(pluginId, channelEvent.Name)] = raw;
        }

        var payload = channelEvent.Payload.ValueKind == JsonValueKind.Undefined ? NoPayload : channelEvent.Payload;
        log.Append(EventKind, null, new { folder = folderId, name = channelEvent.Name, payload });
    }
}

// The `data` of a `plugin` event on the stream, as a desktop reads it.
public sealed record PluginEventFrame(string Folder, string Name, JsonElement Payload);
