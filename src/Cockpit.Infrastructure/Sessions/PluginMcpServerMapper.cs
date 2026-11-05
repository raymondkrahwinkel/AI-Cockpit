using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

internal static class PluginMcpServerMapper
{
    internal static McpToolNames? GatewayToolNames(IReadOnlyList<PluginMcpServer> servers, string? paneId, SessionMcpMounts? mounts)
    {
        if (paneId is null || !servers.Any(server => server.CockpitHosted && server.Name == "cockpit"))
        {
            return null;
        }
        var names = mounts?.Set(paneId)?.ToolNames;
        names?.PreserveDirectServers(servers.Where(server => server.Command is not null).Select(server => server.Name));
        return names;
    }

    internal static PluginMcpServer? ToPluginMcpServer(McpServerConfig server, string? oauthAccessToken, string? oauthProxyUrl, string? agentName = null) => server.Transport switch
    {
        McpTransport.Http when oauthProxyUrl is { Length: > 0 } => new PluginMcpServer
        {
            Name = server.Name,
            Url = oauthProxyUrl,
            Headers = McpAgentHeaders.For(server, null, agentName),
            CockpitHosted = true,
        },
        McpTransport.Http when !string.IsNullOrWhiteSpace(server.Url) => new PluginMcpServer
        {
            Name = server.Name,
            Url = server.Url,
            BearerToken = CockpitMcpBearer.UserCredential(server, oauthAccessToken),
            Headers = McpAgentHeaders.For(server, CockpitMcpBearer.UserCredential(server, oauthAccessToken), agentName),
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
