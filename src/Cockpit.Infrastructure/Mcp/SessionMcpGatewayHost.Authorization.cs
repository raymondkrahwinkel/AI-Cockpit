using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Core.Sessions.Permissions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed partial class SessionMcpGatewayHost
{
    private sealed record ToolAuthorization(Func<string, string, bool>? Allows, string? Ceiling = null, IReadOnlyList<string>? AllowedTools = null);
    private readonly ConcurrentDictionary<string, ToolAuthorization> _toolAuthorization = new(StringComparer.Ordinal);

    internal void SetToolAuthorization(string paneId, Func<string, string, bool>? allows) => _toolAuthorization[paneId] = new(allows);

    internal void SetToolCeiling(string paneId, string ceiling, IReadOnlyList<string> allowedTools) =>
        _toolAuthorization.AddOrUpdate(paneId, _ => new(null, ceiling, allowedTools.ToArray()), (_, previous) => previous with { Ceiling = ceiling, AllowedTools = allowedTools.ToArray() });

    private async ValueTask<CallToolResult> _CallFallbackAsync(string paneId, IMcpToolSession live, RequestContext<CallToolRequestParams> request, Action<string> exposeDirect, CancellationToken ct)
    {
        var input = request.Params.Arguments;
        if (input is null || !input.TryGetValue("name", out var named) || named.ValueKind != JsonValueKind.String || named.GetString() is not { } name)
        {
            return _FallbackError("A tool name from search_tools is required.");
        }
        var tool = live.ForwardTools.FirstOrDefault(tool => tool.ProtocolTool.Name == name);
        var origin = live.GatewayTools.FirstOrDefault(tool => tool.Function.Name == name);
        if (tool is null || origin is null || (input.TryGetValue("server", out var server) && server.ValueKind == JsonValueKind.String && !string.Equals(server.GetString(), origin.ServerName, StringComparison.OrdinalIgnoreCase)))
        {
            return _FallbackError($"Tool '{name}' is not mounted in this session.");
        }
        Dictionary<string, JsonElement>? arguments = null;
        if (input.TryGetValue("arguments", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
        {
            if (supplied.ValueKind != JsonValueKind.Object)
            {
                return _FallbackError("Tool arguments must be a JSON object.");
            }
            arguments = supplied.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        }
        var set = _mounts?.Set(paneId);
        var canonical = set?.ToolNames.ToCanonical("mcp__cockpit__" + name) ?? name;
        _toolAuthorization.TryGetValue(paneId, out var policy);
        var ceiling = policy?.Ceiling ?? (set?.DelegationCeiling is not null ? DelegatedToolPermissionPolicy.ReadOnlyCeiling : null);
        var grade = DelegatedToolPermissionPolicy.Classify(tool.ProtocolTool.Annotations?.ReadOnlyHint, tool.ProtocolTool.Annotations?.DestructiveHint);
        var permission = DelegatedToolPermissionPolicy.Decide(ceiling, grade, canonical, policy?.AllowedTools?.Contains(canonical, StringComparer.Ordinal) == true);
        if (policy?.Allows?.Invoke(canonical, JsonSerializer.Serialize(arguments ?? new Dictionary<string, JsonElement>())) != true
            || (ceiling is not null && !permission.IsAllowed)
            || (set?.DelegationCeiling is { } servers && !servers.Contains(origin.ServerName) && !origin.AlwaysMounted))
        {
            exposeDirect(name);
            return _FallbackError($"'{canonical}' requires direct authorization or exceeds the session ceiling. It is available by name on the next turn; call it directly so its permission rules are applied.");
        }
        var parameters = new CallToolRequestParams { Name = name, Arguments = arguments, Meta = request.Params.Meta };
        return await tool.InvokeAsync(new RequestContext<CallToolRequestParams>(request.Server, request.JsonRpcRequest, parameters), ct);
    }

    private static CallToolResult _FallbackError(string reason) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = reason }],
    };
}
