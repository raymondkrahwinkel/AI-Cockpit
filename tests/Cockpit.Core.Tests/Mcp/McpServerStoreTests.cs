using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Core.Layout;

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

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsServers()
    {
        var store = new McpServerStore(_configFilePath);
        var servers = new List<McpServerConfig>
        {
            new() { Id = "id-filesystem", Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "/data"], Scope = McpServerScope.LocalOnly },
            new() { Id = "id-github", Name = "github", Transport = McpTransport.Http, Url = "https://api.example.com/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "secret" },
            new() { Id = "id-corp", Name = "corp", Transport = McpTransport.Http, Url = "https://corp.example.com/mcp", Auth = McpServerAuth.OAuth, OAuthAuthority = "https://login.example.com", OAuthClientId = "cockpit", OAuthScopes = "openid offline_access depot", Enabled = false },
        };

        await store.SaveAsync(servers);
        var loaded = await store.LoadAsync();

        Assert.Equivalent(servers, loaded);
    }

    [Fact]
    public async Task LoadAsync_ForARowWrittenBeforeIdsExisted_DerivesItsIdFromItsName()
    {
        // AC-403: the id is what a token is filed under, and a row from an older config has none. It answers to the
        // id its own name derives to — the same one the token store reaches a pre-id entry by — so an upgrade keeps
        // the sign-in. Minting a fresh id here instead would be a different id on every read.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "McpServers": [{ "Name": "Corp", "Transport": "Http", "Url": "https://corp.example.com/mcp" }]
            }
            """);

        var loaded = await new McpServerStore(_configFilePath).LoadAsync();

        Assert.Equal(McpServerIdentity.LegacyIdFor("corp"), Assert.Single(loaded).IdentityKey);
    }

    [Fact]
    public async Task SaveAsync_WritesTheDerivedIdOutForARowThatHadNone_SoALaterRenameCannotMoveIt()
    {
        // The moment that matters: the derivation is a function of the name, so it has to be pinned down before the
        // operator changes that name. Saving a legacy row writes the id it was read under, and every save after that
        // carries it unchanged — which is what makes the rename that follows keep its token.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "McpServers": [{ "Name": "corp", "Transport": "Http", "Url": "https://corp.example.com/mcp" }]
            }
            """);

        var store = new McpServerStore(_configFilePath);
        var legacy = Assert.Single(await store.LoadAsync());

        await store.SaveAsync([legacy with { Name = "corp renamed" }]);

        var renamed = Assert.Single(await store.LoadAsync());
        Assert.Equal("corp renamed", renamed.Name);
        Assert.Equal(McpServerIdentity.LegacyIdFor("corp"), renamed.IdentityKey);
    }

    [Fact]
    public async Task SaveAsync_LeavesTheOtherSectionsIntact()
    {
        var layoutStore = new LayoutSettingsStore(_configFilePath);
        await layoutStore.SaveAsync(new LayoutSettings { SingleSessionLayout = true });

        var mcpStore = new McpServerStore(_configFilePath);
        await mcpStore.SaveAsync([new McpServerConfig { Name = "fs", Command = "npx" }]);

        Assert.True((await layoutStore.LoadAsync()).SingleSessionLayout);
        Assert.Equal("fs", Assert.Single(await mcpStore.LoadAsync()).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
