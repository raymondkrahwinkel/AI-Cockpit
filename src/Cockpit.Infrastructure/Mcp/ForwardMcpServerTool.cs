using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class ForwardMcpServerTool : McpServerTool
{
    private readonly Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> _invoke;

    public ForwardMcpServerTool(string name, Tool tool, object route, Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> invoke)
    {
        var copy = JsonSerializer.SerializeToNode(tool) ?? throw new InvalidOperationException("Tool schema unavailable.");
        copy["name"] = name;
        Route = route;
        ProtocolTool = copy.Deserialize<Tool>() ?? throw new InvalidOperationException("Tool schema unavailable.");
        _invoke = invoke;
    }

    public override Tool ProtocolTool { get; }
    private object Route { get; }

    internal static string NameFor(string alias, string name) =>
        $"{new string(alias.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray())}__{name}";

    internal bool Matches(McpServerTool other) => other is ForwardMcpServerTool forward
        && ReferenceEquals(Route, forward.Route)
        && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(ProtocolTool), JsonSerializer.SerializeToNode(other.ProtocolTool));

    public override IReadOnlyList<object> Metadata { get; } = [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default) => _invoke(request, cancellationToken);
}
