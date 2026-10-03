using Cockpit.Plugins.Abstractions.Health;

namespace Cockpit.Plugin.Discord;

internal sealed class DiscordHealthState(TimeProvider time) : IPluginHealthSection
{
    private readonly Lock _gate = new();
    private bool _connected;
    private string? _botName;
    private DiscordDeliveryState _delivery;
    private DateTimeOffset? _lastMessageAt;

    public string Name => "discord";

    public void Connected(string? botName)
    {
        lock (_gate)
        {
            _connected = true;
            _botName = string.IsNullOrWhiteSpace(botName) ? null : botName.Trim();
        }
    }

    public void Disconnected()
    {
        lock (_gate)
        {
            _connected = false;
            _botName = null;
        }
    }

    public void MessageReceived()
    {
        lock (_gate)
        {
            _lastMessageAt = time.GetUtcNow();
        }
    }

    public void DirectMessageDelivered()
    {
        lock (_gate)
        {
            _delivery = DiscordDeliveryState.Ok;
            _lastMessageAt = time.GetUtcNow();
        }
    }

    public void DirectMessageFailed()
    {
        lock (_gate)
        {
            _delivery = DiscordDeliveryState.Failed;
            _lastMessageAt = time.GetUtcNow();
        }
    }

    public PluginHealthReport Read()
    {
        lock (_gate)
        {
            var connection = _connected
                ? _botName is { } name ? $"Online as {name}" : "Online"
                : "Offline";
            var delivery = _delivery switch
            {
                DiscordDeliveryState.Ok => "DM delivery ok",
                DiscordDeliveryState.Failed => "DM delivery failed",
                _ => "DM delivery not tested",
            };
            return new PluginHealthReport(
                _connected,
                [new PluginHealthRow($"{connection} · {delivery}", _connected ? PluginHealthStatus.Ok : PluginHealthStatus.Failed, _lastMessageAt)]);
        }
    }

    private enum DiscordDeliveryState
    {
        Unknown,
        Ok,
        Failed,
    }
}
