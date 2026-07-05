using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Claude;
using Cockpit.Infrastructure.Claude.Permissions;
using Cockpit.Infrastructure.Notifications;
using FluentAssertions;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Persistence round-trip for the permission-rules section of <c>cockpit.json</c>, plus the
/// invariants that rules are isolated per profile and that saving them leaves the profiles and
/// notifications sections intact — all three stores share the one file.
/// </summary>
public class PermissionRuleStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public PermissionRuleStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsEmpty()
    {
        var store = new PermissionRuleStore(_configFilePath);

        var rules = await store.LoadAsync("work");

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_NullProfileLabel_ReturnsEmpty()
    {
        var store = new PermissionRuleStore(_configFilePath);

        var rules = await store.LoadAsync(profileLabel: null);

        rules.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ThenLoadAsync_RoundTripsTheRule()
    {
        var store = new PermissionRuleStore(_configFilePath);
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        await store.AddAsync("work", rule);
        var rules = await store.LoadAsync("work");

        rules.Should().ContainSingle().Which.Should().Be(rule);
    }

    [Fact]
    public async Task AddAsync_IsIdempotentForAnEqualRule()
    {
        var store = new PermissionRuleStore(_configFilePath);
        var rule = PermissionRule.ForWildcard("Bash");

        await store.AddAsync("work", rule);
        await store.AddAsync("work", rule);

        (await store.LoadAsync("work")).Should().ContainSingle();
    }

    [Fact]
    public async Task AddAsync_KeepsRulesIsolatedPerProfile()
    {
        var store = new PermissionRuleStore(_configFilePath);

        await store.AddAsync("work", PermissionRule.ForWildcard("Bash"));
        await store.AddAsync("personal", PermissionRule.ForWildcard("Edit"));

        (await store.LoadAsync("work")).Should().ContainSingle().Which.ToolName.Should().Be("Bash");
        (await store.LoadAsync("personal")).Should().ContainSingle().Which.ToolName.Should().Be("Edit");
    }

    [Fact]
    public async Task AddAsync_NullProfileLabel_IsANoOp()
    {
        var store = new PermissionRuleStore(_configFilePath);

        await store.AddAsync(profileLabel: null, PermissionRule.ForWildcard("Bash"));

        File.Exists(_configFilePath).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_LeavesTheProfilesAndNotificationsSectionsIntact()
    {
        var profileStore = new ClaudeProfileStore(_configFilePath);
        await profileStore.SaveAsync([new ClaudeProfile("work", @"C:\Users\raymo\.claude-work")]);
        var notificationStore = new NotificationSettingsStore(_configFilePath);
        await notificationStore.SaveAsync(new NotificationSettings { WebhookUrl = "https://example/webhook" });

        var ruleStore = new PermissionRuleStore(_configFilePath);
        await ruleStore.AddAsync("work", PermissionRule.ForWildcard("Bash"));

        (await profileStore.LoadAsync()).Should().ContainSingle().Which.Label.Should().Be("work");
        (await notificationStore.LoadAsync()).WebhookUrl.Should().Be("https://example/webhook");
        (await ruleStore.LoadAsync("work")).Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
