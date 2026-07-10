using System.Text.Json;
using FluentAssertions;
using Cockpit.Core.Claude.Permissions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Locks the <c>--mcp-config</c> body shape: an http-type "cockpit" server pointing at the
/// in-process permission endpoint (verified transport against claude.exe 2.1.197).
/// </summary>
public class McpConfigFileTests
{
    [Fact]
    public void Serialize_ProducesHttpCockpitServerPointingAtTheGivenUrl()
    {
        var json = McpConfigFile.Serialize("http://127.0.0.1:5199/mcp");

        using var doc = JsonDocument.Parse(json);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("cockpit");
        server.GetProperty("type").GetString().Should().Be("http");
        server.GetProperty("url").GetString().Should().Be("http://127.0.0.1:5199/mcp");
    }

    [Fact]
    public void ServerName_IsCockpit_SoTheToolResolvesToTheExpectedName()
    {
        McpConfigFile.ServerName.Should().Be("cockpit");
    }

    [Fact]
    public void Serialize_WithBlankUrl_Throws()
    {
        var act = () => McpConfigFile.Serialize("   ");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Serialize_WithRegistry_KeepsCockpitAndMapsStdioAndHttpServers()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx", Args = ["-y", "@modelcontextprotocol/server-filesystem", "D:\\proj"] },
            new() { Name = "remote", Transport = McpTransport.Http, Url = "https://host/mcp", Auth = McpServerAuth.ApiKey, ApiKey = "secret" },
        };

        var json = McpConfigFile.Serialize("http://127.0.0.1:5199/mcp", registry);

        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");

        // The permission server is always present alongside the fanned-out registry.
        servers.GetProperty("cockpit").GetProperty("type").GetString().Should().Be("http");

        var fs = servers.GetProperty("filesystem");
        fs.GetProperty("type").GetString().Should().Be("stdio");
        fs.GetProperty("command").GetString().Should().Be("npx");
        fs.GetProperty("args").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("-y", "@modelcontextprotocol/server-filesystem", "D:\\proj");

        var remote = servers.GetProperty("remote");
        remote.GetProperty("type").GetString().Should().Be("http");
        remote.GetProperty("url").GetString().Should().Be("https://host/mcp");
        remote.GetProperty("headers").GetProperty("Authorization").GetString().Should().Be("Bearer secret");
    }

    [Fact]
    public void Serialize_WithRegistry_ExcludesLocalOnlyServers_ButKeepsAllAndClaudeOnly()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "filesystem", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.LocalOnly },
            new() { Name = "shared", Transport = McpTransport.Stdio, Command = "npx", Scope = McpServerScope.All },
            new() { Name = "claude-only", Transport = McpTransport.Http, Url = "https://x/mcp", Scope = McpServerScope.ClaudeOnly },
        };

        var json = McpConfigFile.Serialize("http://127.0.0.1:5199/mcp", registry);

        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");

        // Claude Code already has file tools, so a LocalOnly server must not fan out to it — All/ClaudeOnly do.
        servers.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("cockpit", "shared", "claude-only");
    }

    [Fact]
    public void Serialize_WithRegistry_SkipsDisabledCollidingAndTargetlessServers()
    {
        var registry = new McpServerConfig[]
        {
            new() { Name = "off", Transport = McpTransport.Stdio, Command = "npx", Enabled = false },
            new() { Name = "cockpit", Transport = McpTransport.Http, Url = "https://evil/mcp" }, // reserved key
            new() { Name = "no-command", Transport = McpTransport.Stdio, Command = "  " },
            new() { Name = "no-url", Transport = McpTransport.Http, Url = null },
        };

        var json = McpConfigFile.Serialize("http://127.0.0.1:5199/mcp", registry);

        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");

        // Only the (untouched) permission server survives; the reserved "cockpit" registry entry did not
        // clobber it, and disabled/targetless servers were dropped.
        servers.GetProperty("cockpit").GetProperty("url").GetString().Should().Be("http://127.0.0.1:5199/mcp");
        servers.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("cockpit");
    }
}
