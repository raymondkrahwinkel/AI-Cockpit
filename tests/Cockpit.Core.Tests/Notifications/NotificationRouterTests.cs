using Cockpit.Core.Notifications;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>The pure presence → channel routing with the two independent switches: present→local toast, away→Discord webhook, each gated on its own toggle.</summary>
public class NotificationRouterTests
{
    // Present goes to the local toast and away to the Discord webhook, each gated on its own switch — and the
    // webhook needs a URL to go to, so "Discord on, nothing configured" is nowhere rather than a failed post.
    [Theory]
    [InlineData(PresenceState.Present, true, false, false, NotificationChannel.Toast)]
    [InlineData(PresenceState.Present, false, true, true, NotificationChannel.None)]
    [InlineData(PresenceState.Away, false, true, true, NotificationChannel.Webhook)]
    [InlineData(PresenceState.Away, true, true, false, NotificationChannel.None)]
    [InlineData(PresenceState.Away, true, false, true, NotificationChannel.None)]
    public void Route_ChoosesTheChannelForThePresence_GatedOnItsOwnSwitch(
        PresenceState presence, bool localEnabled, bool discordEnabled, bool hasWebhookUrl, NotificationChannel expected)
    {
        Assert.Equal(expected, NotificationRouter.Route(presence, localEnabled, discordEnabled, hasWebhookUrl));
    }
}
