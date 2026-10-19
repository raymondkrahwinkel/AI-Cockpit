using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.Core.Tests.Notifications;

// AC-1588: a plugin's system notification is a silent local toast that reports its click, and nothing at all when
// the operator muted that plugin or turned local notifications off.
public sealed class PluginSystemNotificationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
    private readonly _ClickedToast _toast = new();

    public PluginSystemNotificationTests() => Directory.CreateDirectory(_tempDir);

    [Fact]
    public async Task NotifyForPluginAsync_AnAllowedPlugin_ShowsASilentToastAndReportsTheClick()
    {
        var notifier = await _NotifierAsync(new NotificationSettings(), muted: false);

        var clicked = await notifier.NotifyForPluginAsync("depot", new AttentionNotification("Depot", "New invitation"));

        Assert.True(clicked);
        Assert.Equal("New invitation", Assert.Single(_toast.Silent).Body);
        Assert.Equal(0, _toast.Loud);
    }

    [Fact]
    public async Task NotifyForPluginAsync_ThePluginIsMuted_ShowsNothing()
    {
        var notifier = await _NotifierAsync(new NotificationSettings(), muted: true);

        var clicked = await notifier.NotifyForPluginAsync("depot", new AttentionNotification("Depot", "New invitation"));

        Assert.False(clicked);
        Assert.Empty(_toast.Silent);
    }

    [Fact]
    public async Task NotifyForPluginAsync_LocalNotificationsAreOff_ShowsNothing()
    {
        var notifier = await _NotifierAsync(new NotificationSettings { LocalEnabled = false }, muted: false);

        await notifier.NotifyForPluginAsync("depot", new AttentionNotification("Depot", "New invitation"));

        Assert.Empty(_toast.Silent);
    }

    [Fact]
    public async Task SaveNotificationsMutedAsync_RoundTripsAndLeavesTheRestOfTheRegistrationAlone()
    {
        var store = new PluginRegistrationStore(Path.Combine(_tempDir, "cockpit.json"));
        await store.SaveAsync("depot", new PluginRegistration(Enabled: true, PinnedSha256: "the-consented-hash"));

        await store.SaveNotificationsMutedAsync("depot", muted: true);
        var registration = (await store.LoadAllAsync())["depot"];

        Assert.True(registration.NotificationsMuted);
        Assert.Equal("the-consented-hash", registration.PinnedSha256);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private async Task<AttentionNotifier> _NotifierAsync(NotificationSettings settings, bool muted)
    {
        var store = new PluginRegistrationStore(Path.Combine(_tempDir, "cockpit.json"));
        await store.SaveAsync("depot", new PluginRegistration(Enabled: true, PinnedSha256: "abc"));
        await store.SaveNotificationsMutedAsync("depot", muted);

        var settingsStore = Substitute.For<INotificationSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(settings);
        return new AttentionNotifier(
            settingsStore,
            Substitute.For<IPresenceDetector>(),
            _toast,
            Substitute.For<IWebhookNotifier>(),
            store);
    }

    private sealed class _ClickedToast : IToastNotifier
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
