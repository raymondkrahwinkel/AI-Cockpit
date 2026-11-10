using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Mobile;

internal sealed partial class MobileAssistantChannel
{
    private async Task _SessionLifecycleAsync(string type, string? id, JsonElement body)
    {
        IOperatorActionChannel? channel;
        lock (_gate)
        {
            channel = _operatorChannel;
        }
        try
        {
            if (channel is not { CanManageSessions: true })
            {
                throw new UnauthorizedAccessException("Session lifecycle is unavailable on this connection.");
            }
            if (type == "sessions.catalog")
            {
                var catalog = await channel.SessionCatalogAsync().ConfigureAwait(false);
                _Send(type, writer =>
                {
                    writer.WriteString("replyTo", id);
                    writer.WriteString("capability", catalog.Capability);
                    writer.WriteString("epoch", catalog.Epoch);
                    writer.WritePropertyName("desks");
                    JsonSerializer.Serialize(writer, catalog.Desks, _PendingJson);
                    writer.WritePropertyName("profiles");
                    JsonSerializer.Serialize(writer, catalog.Profiles, _PendingJson);
                });
                return;
            }

            var capability = _String(body, "capability") ?? string.Empty;
            var result = type == "session.start"
                ? await channel.StartSessionAsync(_String(body, "requestId") ?? string.Empty, capability,
                    _String(body, "epoch") ?? string.Empty, _String(body, "deskId") ?? string.Empty,
                    _String(body, "profileId") ?? string.Empty).ConfigureAwait(false)
                : await channel.StopSessionAsync(capability, _String(body, "paneId") ?? string.Empty).ConfigureAwait(false);
            _SessionResult(type, id, result);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogWarning("A phone session lifecycle request failed: {Reason}", exception.GetType().Name);
            _SessionResult(type, id, new OperatorSessionResult(false, "The host could not complete this session action. Reload the list."));
        }
    }

    private void _SessionResult(string type, string? id, OperatorSessionResult result) =>
        _Send(type == "session.start" ? "session.started" : type == "session.stop" ? "session.stopped" : "sessions.catalog", writer =>
        {
            writer.WriteString("replyTo", id);
            writer.WriteBoolean("ok", result.Ok);
            writer.WriteString("error", result.Error);
            writer.WriteString("paneId", result.PaneId);
        });
}
