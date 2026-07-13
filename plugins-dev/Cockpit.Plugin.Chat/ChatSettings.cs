using System.Text.Json;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// The channels a flow can send to. Stored here rather than in the flow, because a webhook is a credential: anyone who
/// has it can post as you. In a node's settings it would sit in the flow's JSON, in every backup of it, and on screen
/// whenever someone opened the step. Here, the backup knows to strip it.
/// </summary>
internal sealed class ChatSettings(IPluginStorage storage)
{
    public List<ChatChannel> Channels
    {
        get => _Load();
        set => storage.Set("channels", value.Select(ChannelEntry.From).ToList());
    }

    /// <summary>The channel a step named, or a refusal that lists the ones there are — a flow that posted to the wrong channel is not a mistake that announces itself.</summary>
    public ChatChannel Channel(string name, ChatService service)
    {
        var configured = Channels.Where(channel => channel.IsComplete && channel.Service == service).ToList();

        if (configured.Count == 0)
        {
            throw new InvalidOperationException($"No {service} channel is configured. Add one in the plugin's settings.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return configured.Count == 1
                ? configured[0]
                : throw new InvalidOperationException(
                    $"There are {configured.Count} {service} channels, so this step must say which: {string.Join(", ", configured.Select(channel => channel.Name))}.");
        }

        return configured.FirstOrDefault(channel => string.Equals(channel.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"There is no {service} channel called '{name}'. There is: {string.Join(", ", configured.Select(channel => channel.Name))}.");
    }

    private List<ChatChannel> _Load()
    {
        try
        {
            return (storage.Get<List<ChannelEntry>>("channels") ?? [])
                .Select(entry => entry.ToDomain())
                .ToList();
        }
        catch (JsonException)
        {
            // Settings we cannot read cost you the settings, not the plugin.
            return [];
        }
    }

    /// <summary>On-disk shape. The webhook is called "WebhookUrl" on purpose: the backup's scrubber empties anything whose name says it is a credential, and this is one.</summary>
    internal sealed class ChannelEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Service { get; set; } = nameof(ChatService.Slack);

        public string WebhookUrl { get; set; } = string.Empty;

        public static ChannelEntry From(ChatChannel channel) => new()
        {
            Name = channel.Name,
            Service = channel.Service.ToString(),
            WebhookUrl = channel.WebhookUrl,
        };

        public ChatChannel ToDomain() => new(
            Name,
            Enum.TryParse<ChatService>(Service, ignoreCase: true, out var service) ? service : ChatService.Slack,
            WebhookUrl);
    }
}
