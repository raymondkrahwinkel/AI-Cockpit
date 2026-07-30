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
    public async Task LoadAsync_TwoRowsHandEditedToShareAnId_DoNotBothAnswerToIt()
    {
        // AC-403 review finding. The dialog refuses two servers with the same name, but the id is not shown
        // anywhere, so there is no equivalent gate on it — and copying an mcpServers block to add a second endpoint
        // on the same host, changing the name and URL, leaves the id behind. Sharing an id is sharing a credential:
        // a sign-out on either withdraws it, and for two paths on one host the origin check cannot tell them apart,
        // so one row's bearer would go to the other's address. The first row keeps the id; the second is pushed off.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "McpServers": [
                { "Id": "shared", "Name": "alpha", "Transport": "Http", "Url": "https://one.example.com/alpha" },
                { "Id": "shared", "Name": "beta", "Transport": "Http", "Url": "https://one.example.com/beta" }
              ]
            }
            """);

        var loaded = await new McpServerStore(_configFilePath).LoadAsync();

        Assert.Equal("shared", Assert.Single(loaded, server => server.Name == "alpha").IdentityKey);
        Assert.Equal(McpServerIdentity.LegacyIdFor("beta"), Assert.Single(loaded, server => server.Name == "beta").IdentityKey);
    }

    [Fact]
    public async Task LoadAsync_RowsSharingBothAnIdAndAName_StillEndUpWithDistinctIdentities()
    {
        // The degenerate copy-paste: the block was duplicated and nothing was edited. Falling back to the name gets
        // the second row nowhere, so it lands on an id nothing can ever have filed a token under — "sign in again"
        // rather than two rows quietly holding one credential between them.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "McpServers": [
                { "Id": "shared", "Name": "alpha", "Transport": "Http", "Url": "https://one.example.com/alpha" },
                { "Id": "shared", "Name": "alpha", "Transport": "Http", "Url": "https://one.example.com/alpha" }
              ]
            }
            """);

        var loaded = await new McpServerStore(_configFilePath).LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, loaded.Select(server => server.IdentityKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task LoadAsync_ReadTwice_GivesTheSameIdentitiesBothTimes()
    {
        // The de-duplication has to be a function of the file, not of the read: the dialog's post-save resync and
        // the token lookups are two separate reads of this store, and a row that keyed differently between them
        // would resync against nothing and report itself unsaved.
        await File.WriteAllTextAsync(_configFilePath, """
            {
              "McpServers": [
                { "Id": "shared", "Name": "alpha", "Transport": "Http", "Url": "https://one.example.com/alpha" },
                { "Id": "shared", "Name": "alpha", "Transport": "Http", "Url": "https://one.example.com/alpha" }
              ]
            }
            """);

        var store = new McpServerStore(_configFilePath);

        Assert.Equal(
            (await store.LoadAsync()).Select(server => server.IdentityKey),
            (await store.LoadAsync()).Select(server => server.IdentityKey));
    }

    [Fact]
    public async Task SaveAsync_ForAConfigCarryingNoIdAtAll_StillWritesOneToDisk()
    {
        // Nothing that reaches this store today hands it an idless config — a row read back off disk and a plugin
        // contribution both arrive with one. This is about what lands on disk if something ever does: an entry with
        // an empty id is a row whose identity is a function of its name again, and a rename after that is the
        // orphaned token this ticket removes. Asserted against the file rather than a round-trip, because reading it
        // back re-derives the id and would report success either way.
        await new McpServerStore(_configFilePath).SaveAsync(
            [new McpServerConfig { Name = "corp", Transport = McpTransport.Http, Url = "https://corp.example.com/mcp" }]);

        Assert.Contains(
            $"\"Id\": \"{McpServerIdentity.LegacyIdFor("corp")}\"",
            await File.ReadAllTextAsync(_configFilePath),
            StringComparison.Ordinal);
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
