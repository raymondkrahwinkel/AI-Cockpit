using System.Text.Json;
using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>Pins the Discord webhook body to the <c>{"content":"..."}</c> shape Discord expects.</summary>
public class DiscordWebhookPayloadTests
{
    [Fact]
    public void FromNotification_RendersTitleAndBodyIntoContent()
    {
        var payload = DiscordWebhookPayload.FromNotification(new AttentionNotification("Claude 2", "Needs attention"));

        Assert.Equal("**Claude 2** — Needs attention", payload.Content);
    }

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
