using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The MCP-servers dialog logic (#26): loading the registry into editable rows, add/remove, and
/// persisting the edited list — with per-transport validation and the http/stdio field split in
/// <see cref="EditableMcpServerViewModel.ToConfig"/>.
/// </summary>
public class McpServersViewModelTests
{
    [Fact]
    public void ToConfig_CarriesAPairedNodesCertificatePinThroughAnUnrelatedEdit()
    {
        var paired = new McpServerConfig
        {
            Name = "laptop · cockpit-agents",
            Transport = McpTransport.Http,
            Url = "https://192.168.1.20:7331/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "granted-by-pairing",
            PinnedCertificateFingerprint = "AABBCCDD",
        };

        var row = new EditableMcpServerViewModel(paired);
        row.Name = "laptop · agents";

        // AC-792: there is no control for the pin — it is agreed during a pairing — but this dialog saves every
        // row it holds. Dropping it here would silently unpin a paired node the next time the operator renamed
        // any server, and that node's TLS would then fail outright.
        Assert.Equal("AABBCCDD", row.ToConfig().PinnedCertificateFingerprint);
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
