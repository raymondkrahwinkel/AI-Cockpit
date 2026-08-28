using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

internal static class PluginMcpServerMapper
{
    internal static PluginMcpServer? ToPluginMcpServer(McpServerConfig server, string? oauthAccessToken, string? oauthProxyUrl) => server.Transport switch
    {
        McpTransport.Http when oauthProxyUrl is { Length: > 0 } => new PluginMcpServer
        {
            Name = server.Name,
            Url = oauthProxyUrl,
            Headers = McpAgentHeaders.For(server, null),
            CockpitHosted = true,
        },
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserCredential(server, oauthAccessToken),
            Headers = McpAgentHeaders.For(server, CockpitMcpBearer.UserCredential(server, oauthAccessToken)),
            CockpitHosted = server.CockpitHosted,
        },
        McpTransport.Stdio when !string.IsNullOrWhiteSpace(server.Command) => new PluginMcpServer
        {
            Name = server.Name,
            Command = server.Command,
            Args = server.Args,
        },
        _ => null,
    };
}
