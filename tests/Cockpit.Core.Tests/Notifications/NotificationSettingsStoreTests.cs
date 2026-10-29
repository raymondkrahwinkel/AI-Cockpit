using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.Notifications;

// Layer 2, persistence/notification contract: saved settings and plugin mute preferences govern silent delivery.
public class NotificationSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public NotificationSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task PluginNotifications_RespectPersistedSettingsAndMutePreference()
    {
        var store = new NotificationSettingsStore(_configFilePath);
        var settings = new NotificationSettings
        {
            LocalEnabled = false,
            DiscordEnabled = true,
            WebhookUrl = "https://discord.com/api/webhooks/123/abc",
            IdleThreshold = TimeSpan.FromMinutes(30),
            NotifyOnSessionFinished = false,
            NotifyOnSessionIdle = true,
            NotifyWhenAllSessionsIdle = true,
            // Every flag here is set away from its default on purpose: Assert.Equivalent below cannot tell a value
            // that round-tripped from one that was never written and came back as the default it already had.
            NotifyOnCiFailure = false,
            SessionIdleThreshold = TimeSpan.FromMinutes(12),
            RemoteHealthWatchedInterval = TimeSpan.FromSeconds(17),
            RemoteHealthBackgroundInterval = TimeSpan.FromSeconds(83),
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equivalent(settings, loaded);

        await using var registrations = new PluginRegistrationStore(_configFilePath);
        await registrations.SaveAsync("depot", new PluginRegistration(Enabled: true, PinnedSha256: "the-consented-hash"));
        var toast = new ClickedToast();
        var webhook = Substitute.For<IWebhookNotifier>();
        var notifier = new AttentionNotifier(store, Substitute.For<IPresenceDetector>(), toast, webhook, registrations);
        var notification = new AttentionNotification("Depot", "New invitation");

        Assert.False(await notifier.NotifyForPluginAsync("depot", notification));
        Assert.Empty(toast.Silent);

        await store.SaveAsync(settings with { LocalEnabled = true });
        Assert.True(await notifier.NotifyForPluginAsync("depot", notification));
        Assert.Equal(notification, Assert.Single(toast.Silent));
        Assert.Equal(0, toast.Loud);

        await registrations.SaveNotificationsMutedAsync("depot", muted: true);
        var registration = (await registrations.LoadAllAsync())["depot"];
        Assert.True(registration.NotificationsMuted);
        Assert.Equal("the-consented-hash", registration.PinnedSha256);
        Assert.False(await notifier.NotifyForPluginAsync("depot", notification));
        Assert.Single(toast.Silent);
        Assert.Equivalent(settings with { LocalEnabled = true }, await store.LoadAsync());
        await webhook.DidNotReceiveWithAnyArgs().NotifyAsync(Arg.Any<string>(), Arg.Any<AttentionNotification>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class ClickedToast : IToastNotifier
    {
        public List<AttentionNotification> Silent { get; } = [];

        public int Loud { get; private set; }

        public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
        {
            Loud++;
            return Task.CompletedTask;
        }

        public Task<bool> NotifySilentlyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
        {
            Silent.Add(notification);
            return Task.FromResult(true);
        }
    }
}
