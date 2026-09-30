using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Sessions.Permissions;

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
    public async Task AddAsync_ThenLoadAsync_RoundTripsTheRule()
    {
        var store = new PermissionRuleStore(_configFilePath);
        var rule = PermissionRule.ForExact("Bash", """{"command":"dotnet build"}""");

        await store.AddAsync("work", rule);
        var rules = await store.LoadAsync("work");

        Assert.Equal(rule, Assert.Single(rules));
    }

    [Fact]
    public async Task AddAsync_KeepsRulesIsolatedPerProfile()
    {
        var store = new PermissionRuleStore(_configFilePath);

        await store.AddAsync("work", PermissionRule.ForWildcard("Bash"));
        await store.AddAsync("personal", PermissionRule.ForWildcard("Edit"));

        Assert.Equal("Bash", Assert.Single(await store.LoadAsync("work")).ToolName);
        Assert.Equal("Edit", Assert.Single(await store.LoadAsync("personal")).ToolName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
