namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Hosts cockpit MCP endpoints (#AC-13, #AC-12). Besides the endpoints registered up front (see
/// <see cref="CockpitMcpEndpoint"/>), a plugin can mount one at runtime — it loads after the host has started, so
/// <see cref="MountAsync"/> is how its <c>ICockpitHost.AddMcpEndpoint</c> gets a working, auto-published MCP server
/// for its tools. The host reference is what lets the App's <c>ICockpitHost</c> reach the Infrastructure host.
/// </summary>
public interface ICockpitMcpEndpointHost
{
    /// <summary>
    /// Mounts an MCP endpoint for the already-built <paramref name="tools"/> instance (a class with
    /// <c>[McpServerTool]</c> methods, constructed by the caller with its own dependencies) on a loopback address
    /// and publishes it to the registry under <paramref name="serverName"/> as its own MCP server. Idempotent per
    /// name: mounting a name that is already up is a no-op. <paramref name="enabledByDefault"/> follows the same
    /// on-by-default rule as a registered endpoint.
    /// </summary>
    Task MountAsync(string serverName, object tools, bool enabledByDefault = true, CancellationToken cancellationToken = default);
}
