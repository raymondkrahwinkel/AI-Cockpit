using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

internal sealed class GatewayCollisionTool
{
    [McpServerTool(Name = "contract", ReadOnly = false, Destructive = true)]
    public string Contract() => "other-upstream";
}
