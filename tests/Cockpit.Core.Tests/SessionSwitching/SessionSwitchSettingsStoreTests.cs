using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionSwitching;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Claude.Permissions;
using Cockpit.Infrastructure.Notifications;
using Cockpit.Infrastructure.SessionSwitching;
using FluentAssertions;

namespace Cockpit.Core.Tests.SessionSwitching;

/// <summary>
/// Load/save round-trip for the session-switch section of <c>cockpit.json</c>, plus the invariant
/// that saving one section leaves every sibling section (profiles, notifications, permission rules)
/// intact — all stores share the one file.
/// </summary>
public class SessionSwitchSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public SessionSwitchSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsDefaults()
    {
        var store = new SessionSwitchSettingsStore(_configFilePath);

        var settings = await store.LoadAsync();

        settings.IsEnabled.Should().BeTrue();
        settings.Modifier.Should().Be(SessionSwitchSettings.DefaultModifier);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSettings()
    {
        var store = new SessionSwitchSettingsStore(_configFilePath);
        var settings = new SessionSwitchSettings
        {
            IsEnabled = false,
            Modifier = SessionSwitchModifier.CtrlAlt,
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task SaveAsync_PersistsTheModifierAsItsName()
    {
        var store = new SessionSwitchSettingsStore(_configFilePath);

        await store.SaveAsync(new SessionSwitchSettings { Modifier = SessionSwitchModifier.Alt });

        var json = await File.ReadAllTextAsync(_configFilePath);
        json.Should().Contain("\"Alt\"");
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        // All stores write the same file; saving the session-switch section must not wipe the
        // profiles, notifications or permission rules that the sibling stores wrote before it.
        var profileStore = new ClaudeProfileStore(_configFilePath);
        var profiles = new List<ClaudeProfile> { new("work", @"C:\Users\raymo\.claude-work") };
        await profileStore.SaveAsync(profiles);

        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var permissionStore = new PermissionRuleStore(_configFilePath);
        await permissionStore.AddAsync("work", new PermissionRule("Bash", PermissionRuleScope.Exact, "ls"));

        var switchStore = new SessionSwitchSettingsStore(_configFilePath);
        await switchStore.SaveAsync(new SessionSwitchSettings { IsEnabled = false, Modifier = SessionSwitchModifier.Alt });

        var reloadedProfiles = await profileStore.LoadAsync();
        var reloadedNotifications = await notificationStore.LoadAsync();
        var reloadedRules = await permissionStore.LoadAsync("work");
        var reloadedSwitch = await switchStore.LoadAsync();

        reloadedProfiles.Should().BeEquivalentTo(profiles);
        reloadedNotifications.WebhookUrl.Should().Be("https://example/webhook");
        reloadedRules.Should().ContainSingle(rule => rule.ToolName == "Bash");
        reloadedSwitch.IsEnabled.Should().BeFalse();
        reloadedSwitch.Modifier.Should().Be(SessionSwitchModifier.Alt);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
