using System.Text.Json;
using Cockpit.Core.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>Pins the Discord webhook body to the <c>{"content":"..."}</c> shape Discord expects.</summary>
public class DiscordWebhookPayloadTests
{
    [Fact]
    public void FromNotification_RendersTitleAndBodyIntoContent()
    {
        var payload = DiscordWebhookPayload.FromNotification(new AttentionNotification("Claude 2", "Needs attention"));

        payload.Content.Should().Be("**Claude 2** — Needs attention");
    }

    [Fact]
    public void ToJson_ProducesASingleContentProperty()
    {
        var json = DiscordWebhookPayload.FromNotification(new AttentionNotification("Claude 1", "Done")).ToJson();

        using var document = JsonDocument.Parse(json);
        document.RootElement.TryGetProperty("content", out var content).Should().BeTrue();
        content.GetString().Should().Be("**Claude 1** — Done");
        document.RootElement.EnumerateObject().Should().ContainSingle();
    }
}
