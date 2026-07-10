using Cockpit.Core.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>The pure presence → channel routing with the two independent switches: present→local toast, away→Discord webhook, each gated on its own toggle.</summary>
public class NotificationRouterTests
{
    [Fact]
    public void Route_Present_LocalEnabled_ChoosesToast()
    {
        NotificationRouter.Route(PresenceState.Present, localEnabled: true, discordEnabled: false, hasWebhookUrl: false)
            .Should().Be(NotificationChannel.Toast);
    }

    [Fact]
    public void Route_Present_LocalDisabled_ChoosesNone()
    {
        NotificationRouter.Route(PresenceState.Present, localEnabled: false, discordEnabled: true, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.None);
    }

    [Fact]
    public void Route_Away_DiscordEnabled_WithWebhook_ChoosesWebhook()
    {
        NotificationRouter.Route(PresenceState.Away, localEnabled: false, discordEnabled: true, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.Webhook);
    }

    [Fact]
    public void Route_Away_DiscordEnabled_WithoutWebhook_ChoosesNone()
    {
        NotificationRouter.Route(PresenceState.Away, localEnabled: true, discordEnabled: true, hasWebhookUrl: false)
            .Should().Be(NotificationChannel.None);
    }

    [Fact]
    public void Route_Away_DiscordDisabled_ChoosesNone()
    {
        NotificationRouter.Route(PresenceState.Away, localEnabled: true, discordEnabled: false, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.None);
    }
}
