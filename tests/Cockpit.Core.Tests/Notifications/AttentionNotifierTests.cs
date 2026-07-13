using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
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
    private static readonly AttentionNotification Done = new("Claude 1", "Done");
    private static readonly AttentionNotification Idle = new("Claude 1", "Idle for 5 minute(s)");

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

    [Fact]
    public async Task SessionFinished_WhileWatchingThatSession_DeliversNothing()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings());
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifySessionFinishedAsync(Done, isSelected: true, isWindowActive: true);

        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);
    }

    [Fact]
    public async Task SessionFinished_WhileLookingElsewhere_DeliversToast()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings());
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifySessionFinishedAsync(Done, isSelected: false, isWindowActive: true);

        await _toastNotifier.Received(1).NotifyAsync(Done, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionFinished_WithTheSettingOff_DeliversNothing()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { NotifyOnSessionFinished = false });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifySessionFinishedAsync(Done, isSelected: false, isWindowActive: false);

        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);
    }

    [Fact]
    public async Task SessionIdle_OnlyDeliversWhenAskedFor()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { NotifyOnSessionIdle = false });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifySessionIdleAsync(Idle);
        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);

        _settingsStore.LoadAsync().Returns(new NotificationSettings { NotifyOnSessionIdle = true });

        await NewSut().NotifySessionIdleAsync(Idle);
        await _toastNotifier.Received(1).NotifyAsync(Idle, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllSessionsIdle_OnlyDeliversWhenAskedFor()
    {
        _settingsStore.LoadAsync().Returns(new NotificationSettings { NotifyWhenAllSessionsIdle = false });
        _presenceDetector.GetPresence(Arg.Any<TimeSpan>()).Returns(PresenceState.Present);

        await NewSut().NotifyAllSessionsIdleAsync();
        await _toastNotifier.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default);

        _settingsStore.LoadAsync().Returns(new NotificationSettings { NotifyWhenAllSessionsIdle = true });

        await NewSut().NotifyAllSessionsIdleAsync();
        await _toastNotifier.Received(1).NotifyAsync(
            Arg.Is<AttentionNotification>(notification => notification.Body == "All sessions are idle"),
            Arg.Any<CancellationToken>());
    }
}
