using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class ForwardMcpServerTool : McpServerTool
{
    private readonly Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> _invoke;

    public ForwardMcpServerTool(string alias, Tool tool, Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> invoke)
    {
        var copy = JsonSerializer.SerializeToNode(tool) ?? throw new InvalidOperationException("Tool schema unavailable.");
        copy["name"] = $"{alias}__{tool.Name}";
        ProtocolTool = copy.Deserialize<Tool>() ?? throw new InvalidOperationException("Tool schema unavailable.");
        _invoke = invoke;
    }

    public override Tool ProtocolTool { get; }

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) => _invoke(request, cancellationToken);
}
