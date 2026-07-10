using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// The orchestrator wiring: given the loaded settings and the detected presence, it must deliver on
/// exactly the channel the pure router picks — present → toast, away → webhook, disabled → neither.
/// </summary>
public class AttentionNotifierTests
{
    private readonly INotificationSettingsStore _settingsStore = Substitute.For<INotificationSettingsStore>();
    private readonly IPresenceDetector _presenceDetector = Substitute.For<IPresenceDetector>();
    private readonly IToastNotifier _toastNotifier = Substitute.For<IToastNotifier>();
    private readonly IWebhookNotifier _webhookNotifier = Substitute.For<IWebhookNotifier>();

    private static readonly AttentionNotification Notification = new("Claude 1", "Needs attention");

    private AttentionNotifier NewSut() =>
        new(_settingsStore, _presenceDetector, _toastNotifier, _webhookNotifier);

    [Fact]
    public async Task Present_DeliversToast_NotWebhook()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { WebhookUrl = "https://example/webhook" });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifyAttentionAsync(Notification);

        await _toastNotifier.Received(1).NotifyAsync(Notification, Arg.Any<CancellationToken>());
        await _webhookNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Away_WithWebhook_DeliversWebhook_WithConfiguredUrl_NotToast()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { DiscordEnabled = true, WebhookUrl = "https://example/webhook" });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Away);

        await NewSut().NotifyAttentionAsync(Notification);

        await _webhookNotifier.Received(1).NotifyAsync("https://example/webhook", Notification, Arg.Any<CancellationToken>());
        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);
    }

    [Fact]
    public async Task Disabled_DeliversNothing()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { LocalEnabled = false, DiscordEnabled = false, WebhookUrl = "https://example/webhook" });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Away);

        await NewSut().NotifyAttentionAsync(Notification);

        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);
        await _webhookNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default!, default);
    }
}
