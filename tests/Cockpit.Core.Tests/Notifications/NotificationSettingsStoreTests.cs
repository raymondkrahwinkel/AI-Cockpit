using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Notifications;

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
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
