using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// Which bearer a session presents to an MCP server (AC-40): this run's key for a cockpit-hosted loopback endpoint,
/// the server's own API key for a user API-key server, and — the part that keeps the host's key from leaking — none
/// for anything else, including a plain user-added loopback server.
/// </summary>
public class CockpitMcpBearerTests
{
    [Fact]
    public void For_ACockpitHostedEndpoint_GetsThisRunsKey()
    {
        var key = new McpAuthKey();
        var server = new McpServerConfig { Name = "cockpit-session", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", CockpitHosted = true };

        CockpitMcpBearer.For(server, key).Should().Be(key.Value);
    }

    [Fact]
    public void For_AUserApiKeyServer_GetsItsOwnKey_NotTheHostsKey()
    {
        var key = new McpAuthKey();
        var server = new McpServerConfig
        {
            Name = "youtrack",
            Transport = McpTransport.Http,
            Url = "http://example/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "the-servers-own-key",
        };

        CockpitMcpBearer.For(server, key).Should().Be("the-servers-own-key");
    }

    [Fact]
    public void For_APlainUserAddedServer_GetsNoKey_SoTheHostsKeyNeverLeaksToAThirdParty()
    {
        var key = new McpAuthKey();
        var server = new McpServerConfig { Name = "local", Transport = McpTransport.Http, Url = "http://127.0.0.1:9/mcp" };

        CockpitMcpBearer.For(server, key).Should().BeNull();
    }

    // The spawned-CLI path (adapters) writes only a user server's own key as a literal; a cockpit-hosted endpoint's
    // auth rides the COCKPIT_MCP_KEY env var, so UserApiKey deliberately returns null for it — no literal on disk.
    [Fact]
    public void UserApiKey_ReturnsAUserServersOwnKey_ButNothingForACockpitHostedEndpoint()
    {
        var apiKeyServer = new McpServerConfig
        {
            Name = "youtrack",
            Transport = McpTransport.Http,
            Url = "http://example/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "the-servers-own-key",
        };
        var cockpitHosted = new McpServerConfig { Name = "cockpit-session", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", CockpitHosted = true };

        CockpitMcpBearer.UserApiKey(apiKeyServer).Should().Be("the-servers-own-key");
        CockpitMcpBearer.UserApiKey(cockpitHosted).Should().BeNull();
    }
}
