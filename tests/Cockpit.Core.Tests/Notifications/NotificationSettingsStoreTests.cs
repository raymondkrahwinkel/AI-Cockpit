using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Load/save round-trip for the notification section of <c>cockpit.json</c>, plus the key invariant
/// that saving one section leaves the other section (profiles) intact — both stores share the file.
/// </summary>
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
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new NotificationSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.LocalEnabled.Should().BeTrue();
        settings.DiscordEnabled.Should().BeFalse();
        settings.WebhookUrl.Should().BeNull();
        settings.IdleThreshold.Should().Be(NotificationSettings.DefaultIdleThreshold);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
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
            SessionIdleThreshold = TimeSpan.FromMinutes(12),
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(settings);
    }

    // 0 minutes means "never let a session go idle" — a real choice, so it must survive the round-trip instead of
    // being read back as the default the way an empty away-threshold is.
    [Fact]
    public async Task SaveAsync_ZeroSessionIdleMinutes_RoundTripsAsOff()
    {
        var store = new NotificationSettingsStore(_configFilePath);

        await store.SaveAsync(new NotificationSettings { SessionIdleThreshold = TimeSpan.Zero });
        var loaded = await store.LoadAsync();

        loaded.SessionIdleThreshold.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheProfilesSectionIntact()
    {
        // Both stores write the same file; saving notifications must not wipe the profiles a
        // previously-saved SessionProfileStore wrote, and vice versa.
        var profileStore = new SessionProfileStore(_configFilePath);
        var profiles = new List<SessionProfile> { new("work", ClaudePluginProfile.Create(@"C:\Users\raymo\.claude-work", null)) };
        await profileStore.SaveAsync(profiles);

        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var reloadedProfiles = await profileStore.LoadAsync();
        var reloadedSettings = await notificationStore.LoadAsync();

        reloadedProfiles.Should().BeEquivalentTo(profiles);
        reloadedSettings.WebhookUrl.Should().Be("https://example/webhook");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
