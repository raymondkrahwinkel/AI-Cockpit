using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
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
        return mounts?.Set(paneId)?.ToolNames;
    }

}
