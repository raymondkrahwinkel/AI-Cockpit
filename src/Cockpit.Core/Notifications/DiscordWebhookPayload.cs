using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Notifications;

/// <summary>
/// The Discord webhook request body shape: <c>{"content":"..."}</c>. A Discord webhook accepts a
/// plain JSON object with a <c>content</c> string; this is the smallest payload that posts a
/// message. Kept in Core with its own serializer so the exact wire shape is unit-testable without
/// an HTTP round-trip.
/// </summary>
public sealed class DiscordWebhookPayload
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>Builds the payload from a notification, rendering it as a single "**Title** — Body" line.</summary>
    public static DiscordWebhookPayload FromNotification(AttentionNotification notification) =>
        new() { Content = $"**{notification.Title}** — {notification.Body}" };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
