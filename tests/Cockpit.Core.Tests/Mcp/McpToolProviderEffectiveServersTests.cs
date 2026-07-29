using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The local-session server set (#26): built-in defaults (filesystem etc.) are always present, a registry
/// entry overrides the built-in of the same name, and Claude-only servers never enter the local tool-loop.
/// </summary>
public class McpToolProviderEffectiveServersTests
{
    [Fact]
    public void EffectiveServers_WithEmptyRegistry_AreTheBuiltInDefaults()
    {
        var effective = McpToolProvider._EffectiveServers([]);

        Assert.Contains("filesystem", effective.Select(server => server.Name));
        Assert.Equivalent(McpServerPresets.LocalDefaults, effective);
    }

    [Fact]
    public void EffectiveServers_RegistryEntry_OverridesTheBuiltInOfTheSameName()
    {
        var custom = new McpServerConfig { Name = "filesystem", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "D:\\only-this"] };

        var effective = McpToolProvider._EffectiveServers([custom]);

        // One filesystem, and it is the registry's (retargeted) one, not the default user-folder root.
        var single = Assert.Single(effective, server => server.Name == "filesystem");
        Assert.Contains("D:\\only-this", single.Args);
    }

    [Fact]
    public void EffectiveServers_ExcludesClaudeOnlyRegistryServers()
    {
        var claudeOnly = new McpServerConfig { Name = "claude-thing", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly };

        var effective = McpToolProvider._EffectiveServers([claudeOnly]);

        Assert.DoesNotContain(effective, server => server.Name == "claude-thing");
    }
}
