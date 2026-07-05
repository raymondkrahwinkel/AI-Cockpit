using Cockpit.Core.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>The pure presence → channel routing: present-toast / away-webhook, plus the disabled and no-webhook fallbacks.</summary>
public class NotificationRouterTests
{
    [Fact]
    public void Route_Present_ChoosesToast()
    {
        NotificationRouter.Route(PresenceState.Present, isEnabled: true, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.Toast);
    }

    [Fact]
    public void Route_Away_WithWebhook_ChoosesWebhook()
    {
        NotificationRouter.Route(PresenceState.Away, isEnabled: true, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.Webhook);
    }

    [Fact]
    public void Route_Away_WithoutWebhook_ChoosesNone()
    {
        // No webhook configured: don't silently route an away operator to a toast they can't see.
        NotificationRouter.Route(PresenceState.Away, isEnabled: true, hasWebhookUrl: false)
            .Should().Be(NotificationChannel.None);
    }

    [Theory]
    [InlineData(PresenceState.Present)]
    [InlineData(PresenceState.Away)]
    public void Route_Disabled_AlwaysChoosesNone(PresenceState presence)
    {
        NotificationRouter.Route(presence, isEnabled: false, hasWebhookUrl: true)
            .Should().Be(NotificationChannel.None);
    }
}
