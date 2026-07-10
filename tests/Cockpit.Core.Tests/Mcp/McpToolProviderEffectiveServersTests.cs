using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;

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

        effective.Select(server => server.Name).Should().Contain("filesystem");
        effective.Should().BeEquivalentTo(McpServerPresets.LocalDefaults);
    }

    [Fact]
    public void EffectiveServers_RegistryEntry_OverridesTheBuiltInOfTheSameName()
    {
        var custom = new McpServerConfig { Name = "filesystem", Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "D:\\only-this"] };

        var effective = McpToolProvider._EffectiveServers([custom]);

        // One filesystem, and it is the registry's (retargeted) one, not the default user-folder root.
        effective.Where(server => server.Name == "filesystem").Should().ContainSingle()
            .Which.Args.Should().Contain("D:\\only-this");
    }

    [Fact]
    public void EffectiveServers_ExcludesClaudeOnlyRegistryServers()
    {
        var claudeOnly = new McpServerConfig { Name = "claude-thing", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly };

        var effective = McpToolProvider._EffectiveServers([claudeOnly]);

        effective.Should().NotContain(server => server.Name == "claude-thing");
    }
}
