using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

internal sealed class GatewayContractTool
{
    [McpServerTool(Name = "contract", ReadOnly = true, Destructive = false, OutputSchemaType = typeof(Dictionary<string, string>))]
    public CallToolResult Contract() => new()
    {
        Content = [new TextContentBlock { Text = "first" }, new ImageContentBlock { Data = "aGVsbG8="u8.ToArray(), MimeType = "image/png", Annotations = new() { Priority = 0.7f } }, new TextContentBlock { Text = "last" }],
        Meta = new System.Text.Json.Nodes.JsonObject { ["fixture"] = "retained" },
    };
}
