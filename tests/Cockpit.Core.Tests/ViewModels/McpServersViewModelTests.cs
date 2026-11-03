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

    private sealed class FakeInternalMcpProvider(params McpServerConfig[] servers) : ICockpitInternalMcpProvider
    {
        public IReadOnlyList<McpServerConfig> GetServers() => servers;

        public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses() => [];
    }
}
