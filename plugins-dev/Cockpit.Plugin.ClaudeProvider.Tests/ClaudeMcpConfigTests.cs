using System.Text.Json.Nodes;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeMcpConfig"/> (#26): the shared MCP registry written into Claude's <c>--mcp-config</c>. The
/// load-bearing property (AC-40) is that a cockpit-hosted endpoint's auth is written as an <em>env-var reference</em>
/// (<c>${COCKPIT_MCP_KEY}</c>), never a literal key, so nothing sensitive lands in the file — while a user API-key
/// server still gets its own literal bearer.
/// </summary>
public class ClaudeMcpConfigTests
{
    [Fact]
    public void Write_ForACockpitHostedServer_ReferencesTheAuthKeyEnvVar_NotALiteral()
    {
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "cockpit-session", Url = "http://127.0.0.1:1/mcp", CockpitHosted = true }]);

        try
        {
            _Authorization(path!, "cockpit-session").Should().Be("Bearer ${COCKPIT_MCP_KEY}");
        }
        finally
        {
            File.Delete(path!);
        }
    }

    [Fact]
    public void Write_ForAUserApiKeyServer_WritesItsOwnBearerLiteral()
    {
        var path = ClaudeMcpConfig.Write([new PluginMcpServer { Name = "youtrack", Url = "http://example/mcp", BearerToken = "yt-key" }]);

        try
        {
            _Authorization(path!, "youtrack").Should().Be("Bearer yt-key");
        }
        finally
        {
            File.Delete(path!);
        }
    }

    private static string _Authorization(string path, string serverName) =>
        JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]![serverName]!["headers"]!["Authorization"]!.GetValue<string>();
}
