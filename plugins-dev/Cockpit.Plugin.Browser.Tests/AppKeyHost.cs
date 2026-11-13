using System.Reflection;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Browser.Tests;

public class AppKeyHost : DispatchProxy
{
    public string? Caller { get; set; }
    private readonly Dictionary<string, object?> _members = new()
    {
        ["get_CurrentMcpCaller"] = null,
        ["get_Channel"] = new AppKeyChannel(),
        ["get_Sessions"] = NullCockpitSessionObserver.Instance,
    };

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == "get_CurrentMcpCaller" && Caller is { } caller
            ? new Cockpit.Plugins.Abstractions.Mcp.McpCaller(caller, Cockpit.Plugins.Abstractions.Mcp.McpCallerKind.Session)
            : _members.TryGetValue(targetMethod?.Name ?? "", out var member) ? member : throw new InvalidOperationException("An app key must not reach browser resources.");

    private sealed class AppKeyChannel : IPluginBackendChannel, IDisposable
    {
        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) => this;
        public void Publish(string name, JsonElement payload) => throw new InvalidOperationException("An app key must not dispatch browser requests.");
        public void Dispose() { }
    }
}
