using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>The pure presence → channel routing with the two independent switches: present→local toast, away→Discord webhook, each gated on its own toggle.</summary>
public class NotificationRouterTests
{
    [Fact]
    public void Route_Present_LocalEnabled_ChoosesToast()
    {
        Assert.Equal(NotificationChannel.Toast, NotificationRouter.Route(PresenceState.Present, localEnabled: true, discordEnabled: false, hasWebhookUrl: false));
    }

    [Fact]
    public void Route_Present_LocalDisabled_ChoosesNone()
    {
        Assert.Equal(NotificationChannel.None, NotificationRouter.Route(PresenceState.Present, localEnabled: false, discordEnabled: true, hasWebhookUrl: true));
    }

    [Fact]
    public void Route_Away_DiscordEnabled_WithWebhook_ChoosesWebhook()
    {
        Assert.Equal(NotificationChannel.Webhook, NotificationRouter.Route(PresenceState.Away, localEnabled: false, discordEnabled: true, hasWebhookUrl: true));
    }

    [Fact]
    public void Route_Away_DiscordEnabled_WithoutWebhook_ChoosesNone()
    {
        Assert.Equal(NotificationChannel.None, NotificationRouter.Route(PresenceState.Away, localEnabled: true, discordEnabled: true, hasWebhookUrl: false));
    }

    [Fact]
    public void Route_Away_DiscordDisabled_ChoosesNone()
    {
        Assert.Equal(NotificationChannel.None, NotificationRouter.Route(PresenceState.Away, localEnabled: true, discordEnabled: false, hasWebhookUrl: true));
    }
}
