using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Layout;
using FluentAssertions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>Load/save round-trip for the shared MCP-server registry (#26) in <c>cockpit.json</c>, plus the invariant that saving it leaves sibling sections intact.</summary>
public class McpServerStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFilePath;

    public McpServerStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configFilePath = Path.Combine(_tempDir, "cockpit.json");
    }

    [Fact]
    public async Task LoadAsync_NoConfigFile_ReturnsEmpty()
    {
        var store = new McpServerStore(_configFilePath);

        (await store.LoadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsServers()
    {
        var store = new McpServerStore(_configFilePath);
        var servers = new List<McpServerConfig>
        {
            new() { Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "/data"] },
            new() { Name = "github", Transport = McpTransport.Http, Url = "https://api.example.com/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "secret" },
            new() { Name = "corp", Transport = McpTransport.Http, Url = "https://corp.example.com/mcp", Auth = McpServerAuth.OAuth, OAuthAuthority = "https://login.example.com", OAuthClientId = "cockpit", Enabled = false },
        };

        await store.SaveAsync(servers);
        var loaded = await store.LoadAsync();

        loaded.Should().BeEquivalentTo(servers);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var mcpStore = new McpServerStore(_configFilePath);
        await mcpStore.SaveAsync([new McpServerConfig { Name = "fs", Command = "npx" }]);

        (await layoutStore.LoadAsync()).SingleSessionLayout.Should().BeTrue();
        (await mcpStore.LoadAsync()).Should().ContainSingle().Which.Name.Should().Be("fs");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
