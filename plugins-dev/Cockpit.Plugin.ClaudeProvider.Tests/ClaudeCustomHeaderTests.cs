using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>Custom headers (AC-354) on the Claude route: a server that wants <c>X-Api-Key</c> rather than a bearer.</summary>
public class ClaudeCustomHeaderTests
{
    private static JsonElement _Headers(string path, string serverName) =>
        JsonDocument.Parse(File.ReadAllText(path))
            .RootElement
            .GetProperty("mcpServers")
            .GetProperty(serverName)
            .GetProperty("headers");

    private static void _WithConfig(PluginMcpServer server, Action<string> assert)
    {
        var path = ClaudeMcpConfig.Write([server]);
        Assert.NotNull(path);

        try
        {
            assert(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Write_CarriesACustomHeader()
    {
        var server = new PluginMcpServer
        {
            Name = "private-api",
            Url = "https://api.example/mcp",
            Headers = new Dictionary<string, string> { ["X-Api-Key"] = "the-key" },
        };

        _WithConfig(server, path =>
            Assert.Equal("the-key", _Headers(path, "private-api").GetProperty("X-Api-Key").GetString()));
    }

    [Fact]
    public void Write_CarriesACustomHeaderBesideTheBearer()
    {
        var server = new PluginMcpServer
        {
            Name = "private-api",
            Url = "https://api.example/mcp",
            BearerToken = "the-token",
            Headers = new Dictionary<string, string> { ["X-Tenant"] = "acme" },
        };

        // A server can want both: a bearer for who you are and a tenant header for which account. The host decides
        // what goes in the header set; this route only has to carry all of it.
        _WithConfig(server, path =>
        {
            var headers = _Headers(path, "private-api");
            Assert.Equal("acme", headers.GetProperty("X-Tenant").GetString());
            Assert.Equal("Bearer the-token", headers.GetProperty("Authorization").GetString());
        });
    }

    [Fact]
    public void Write_WithNothingToSend_WritesNoHeadersObject()
    {
        var server = new PluginMcpServer { Name = "plain", Url = "https://api.example/mcp" };

        _WithConfig(server, path =>
        {
            var entry = JsonDocument.Parse(File.ReadAllText(path))
                .RootElement
                .GetProperty("mcpServers")
                .GetProperty("plain");

            Assert.False(entry.TryGetProperty("headers", out _));
        });
    }
}
