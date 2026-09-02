using System.Text.Json;
using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>Pins the Discord webhook body to the <c>{"content":"..."}</c> shape Discord expects.</summary>
public class DiscordWebhookPayloadTests
{
    // The title and body are rendered into the one property Discord reads, and there is nothing else in the body.
    [Fact]
    public void ToJson_ProducesASingleContentProperty()
    {
        var json = DiscordWebhookPayload.FromNotification(new AttentionNotification("Claude 1", "Done")).ToJson();

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("content", out var content));
        Assert.Equal("**Claude 1** — Done", content.GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }
}
