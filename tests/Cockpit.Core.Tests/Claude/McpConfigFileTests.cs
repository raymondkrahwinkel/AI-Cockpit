using System.Text.Json;
using FluentAssertions;
using Cockpit.Core.Claude.Permissions;

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
}
