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

    private sealed class FakeInternalMcpProvider(params McpServerConfig[] servers) : ICockpitInternalMcpProvider
    {
        public IReadOnlyList<McpServerConfig> GetServers() => servers;

        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() => [];
    }
}
