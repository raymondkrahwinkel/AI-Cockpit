using System.Text.Json;
using FluentAssertions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Locks the registry-only <c>--mcp-config</c> body shape (<see cref="McpConfigFile.SerializeRegistryOnly"/>): the
/// enabled, agent-eligible registry servers mapped to the CLI's <c>mcpServers</c> shape, with no cockpit permission
/// server (that endpoint, and the host-side <c>Serialize(mcpUrl,…)</c> overloads that injected it, were removed in
/// AC-46).
/// </summary>
public class McpConfigFileTests
{
    [Fact]
    public void ServerName_IsCockpit_SoTheReservedKeyIsNeverClaimedByTheRegistry()
    {
        McpConfigFile.ServerName.Should().Be("cockpit");
    }

    [Fact]
    public void SerializeRegistryOnly_MapsEligibleServers_WithoutTheCockpitPermissionServer()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx", Args = ["-y", "srv"] },
            new() { Name = "remote", Transport = McpTransport.Http, Url = "https://host/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "secret" },
        };

        var json = McpConfigFile.SerializeRegistryOnly(registry);

        json.Should().NotBeNull();
        using var doc = JsonDocument.Parse(json!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        // No cockpit permission server — the interactive TUI handles permission prompts itself.
        servers.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("filesystem", "remote");
        servers.GetProperty("remote").GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer secret");
    }

    [Fact]
    public void SerializeRegistryOnly_ExcludesLocalOnlyDisabledAndReserved_KeepsAllAndClaudeOnly()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "local", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.LocalOnly },
            new() { Name = "off", Transport = McpTransport.Stdio, Command = "npx", Enabled = false },
            new() { Name = "cockpit", Transport = McpTransport.Http, Url = "https://evil/mcp" },
            new() { Name = "keep", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly },
        };

        var json = McpConfigFile.SerializeRegistryOnly(registry);

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("mcpServers").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("keep");
    }

    [Fact]
    public void SerializeRegistryOnly_WithNoEligibleServers_ReturnsNull()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "local", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.LocalOnly },
            new() { Name = "off", Transport = McpTransport.Stdio, Command = "npx", Enabled = false },
        };

        McpConfigFile.SerializeRegistryOnly(registry).Should().BeNull();
    }
}
