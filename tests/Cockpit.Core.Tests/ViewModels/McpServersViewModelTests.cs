using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The MCP-servers dialog logic (#26): loading the registry into editable rows, add/remove, and
/// persisting the edited list — with per-transport validation and the http/stdio field split in
/// <see cref="EditableMcpServerViewModel.ToConfig"/>.
/// </summary>
public class McpServersViewModelTests
{
    [Fact]
    public async Task RemovingAnEditableServer_PreservesManagedNodeConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cockpit.json");
            await File.WriteAllTextAsync(path, """
                { "McpServers": [
                  { "Name": "Server · cockpit-node", "Transport": "Http", "Url": "https://node.example/mcp", "Auth": "ApiKey", "ApiKey": "paired-key", "PinnedCertificateFingerprint": "AABBCCDD" },
                  { "Name": "Managed endpoint", "IsNodeConnection": true, "Transport": "Http", "Url": "https://other.example/mcp" },
                  { "Name": "mine", "Command": "npx" }
                ] }
                """);
            var store = new McpServerStore(path);
            var editor = new McpServersViewModel(store, []);
            await editor.LoadAsync();

            // L2 data loss: removing through the MCP editor must leave server connection wiring intact.
            Assert.Equal("mine", Assert.Single(editor.Servers).Name);
            editor.RemoveServerCommand.Execute(null);
            Assert.True(await editor.PersistAsync());

            var remaining = await store.LoadAsync();
            Assert.Equal(2, remaining.Count);
            var node = Assert.Single(remaining, server => server.Name == "Server · cockpit-node");
            Assert.Equal(McpTransport.Http, node.Transport);
            Assert.Equal("https://node.example/mcp", node.Url);
            Assert.Equal("paired-key", node.ApiKey);
            Assert.Equal("AABBCCDD", node.PinnedCertificateFingerprint);
            using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.True(saved.RootElement.GetProperty("McpServers")[0].GetProperty("IsNodeConnection").GetBoolean());
            await editor.LoadAsync();
            Assert.Empty(editor.Servers);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ToConfig_DropsTheCertificatePinWhenTheRowStopsBeingAnHttpServer()
    {
        var row = new EditableMcpServerViewModel(new McpServerConfig
        {
            Name = "laptop · cockpit-agents",
            Transport = McpTransport.Http,
            Url = "https://192.168.1.20:7331/mcp",
            PinnedCertificateFingerprint = "AABBCCDD",
        });

        row.Transport = McpTransport.Stdio;

        // A stdio server has no TLS connection for a pin to bind to; keeping it would be a claim about a
        // certificate nothing will ever present.
        Assert.Null(row.ToConfig().PinnedCertificateFingerprint);
    }

    [Fact]
    public void ToConfig_ForHttpApiKey_KeepsUrlAndKey_AndDropsStdioFields()
    {
        var editable = new EditableMcpServerViewModel(new McpServerConfig
        {
            Name = "x",
            Transport = McpTransport.Http,
            Url = "https://x/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "k",
        })
        {
            Command = "npx",     // stale stdio values that must be dropped for http
            Args = "-y\nfoo",
        };

        var config = editable.ToConfig();

        Assert.Null(config.Command);
        Assert.Empty(config.Args);
        Assert.Equal("https://x/mcp", config.Url);
        Assert.Equal(McpServerAuth.ApiKey, config.Auth);
        Assert.Equal("k", config.ApiKey);
    }

    [Fact]
    public void ToConfig_ForOAuth_KeepsTrimmedScopesOverride_AndDropsItWhenAuthIsNotOAuth()
    {
        var editable = new EditableMcpServerViewModel(new McpServerConfig
        {
            Name = "depot",
            Transport = McpTransport.Http,
            Url = "https://depot.example/mcp",
            Auth = McpServerAuth.OAuth,
            OAuthScopes = "depot offline_access",
        });

        Assert.Equal("depot offline_access", editable.OAuthScopes);

        editable.OAuthScopes = "  depot offline_access  ";
        Assert.Equal("depot offline_access", editable.ToConfig().OAuthScopes);

        // A row that used to be OAuth and got switched to another auth mode must not carry a stale scopes override
        // along — the same rule ToConfig already applies to OAuthAuthority/OAuthClientId.
        editable.Auth = McpServerAuth.ApiKey;
        Assert.Null(editable.ToConfig().OAuthScopes);
    }

    private sealed class FakeInternalMcpProvider(params McpServerConfig[] servers) : ICockpitInternalMcpProvider
    {
        public IReadOnlyList<McpServerConfig> GetServers() => servers;

        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() => [];
    }
}
