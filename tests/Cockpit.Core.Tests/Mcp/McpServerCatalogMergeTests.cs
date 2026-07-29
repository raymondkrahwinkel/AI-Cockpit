using Cockpit.App.Plugins;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The effective MCP set the session fan-out and the New-session checklist read (#26, AC-11): the registry with
/// each active plugin's own servers merged in. The manager keeps reading the registry alone, so this merge is what
/// makes a plugin's servers visible to sessions without ever entering the manager.
/// </summary>
public class McpServerCatalogMergeTests
{
    [Fact]
    public void Merge_KeepsRegistryServersAndAppendsPluginServers()
    {
        var registry = new[] { _Server("filesystem"), _Server("cockpit-orchestrator") };
        var plugin = new[] { _Server("YouTrack: Personal") };

        var merged = McpServerCatalog.Merge(registry, plugin);

        Assert.Equal(new[] { "filesystem", "cockpit-orchestrator", "YouTrack: Personal" }, merged.Select(server => server.Name));
    }

    [Fact]
    public void Merge_WhenAPluginOwnsANameStillInTheRegistry_ThePluginWins()
    {
        // The one-start-after-upgrade case: the pre-AC-11 push left "YouTrack: Personal" in the registry, and the
        // plugin now provides it live. The plugin's is authoritative, and there must be exactly one.
        var registry = new[] { _Server("YouTrack: Personal", url: "https://stale.example/mcp") };
        var plugin = new[] { _Server("YouTrack: Personal", url: "https://live.example/mcp") };

        var merged = McpServerCatalog.Merge(registry, plugin);

        Assert.Equal("https://live.example/mcp", Assert.Single(merged).Url);
    }

    [Fact]
    public void Merge_WithNoPluginServers_IsJustTheRegistry()
    {
        var registry = new[] { _Server("filesystem") };

        Assert.Equivalent(registry, McpServerCatalog.Merge(registry, []));
    }

    private static McpServerConfig _Server(string name, string? url = null) =>
        new() { Name = name, Transport = McpTransport.Http, Url = url ?? $"https://example/{name}" };
}
