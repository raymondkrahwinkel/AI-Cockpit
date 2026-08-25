using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Notifications;

// The Discord webhook request body shape: `{"content":"..."}`. Kept in Core with its own
// serializer so the exact wire shape is unit-testable without an HTTP round-trip.
public sealed class DiscordWebhookPayload
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    // Builds the payload from a notification, rendering it as a single "**Title** — Body" line.
    public static DiscordWebhookPayload FromNotification(AttentionNotification notification) =>
        new() { Content = $"**{notification.Title}** — {notification.Body}" };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
