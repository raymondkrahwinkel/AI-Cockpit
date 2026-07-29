using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>Minimal MCP tool hosted by the in-process test server for <see cref="McpToolProviderInvokeAsyncTests"/> (AC-502).</summary>
internal sealed class McpTestToolInvoke
{
    [McpServerTool(Name = "echo")]
    public string Echo(string text) => text;

    [McpServerTool(Name = "boom")]
    public string Boom() => throw new InvalidOperationException("this tool always fails");
}
