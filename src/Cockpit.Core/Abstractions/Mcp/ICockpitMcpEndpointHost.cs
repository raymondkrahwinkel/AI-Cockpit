namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Hosts cockpit MCP endpoints (#AC-13, #AC-12). Besides the endpoints registered up front (see
/// <see cref="CockpitMcpEndpoint"/>), a plugin can mount one at runtime — it loads after the host has started, so
/// <see cref="MountAsync"/> is how its <c>ICockpitHost.AddMcpEndpoint</c> gets a working, key-guarded loopback MCP
/// server for its tools — the cockpit's own, answered live to the session fan-out rather than written to the
/// operator's registry (AC-40). The host reference is what lets the App's <c>ICockpitHost</c> reach the
/// Infrastructure host.
/// </summary>
public interface ICockpitMcpEndpointHost
{
    /// <summary>
    /// Mounts an MCP endpoint for the already-built <paramref name="tools"/> instance (a class with
    /// <c>[McpServerTool]</c> methods, constructed by the caller with its own dependencies) on a loopback address
    /// under <paramref name="serverName"/>. The endpoint is the cockpit's own, not written to the operator's registry
    /// (AC-40): the session fan-out sees it live. Idempotent per name: mounting a name that is already up is a no-op.
    /// <paramref name="isEnabled"/> lets a plugin gate its endpoint on its own setting — read each time the servers
    /// are gathered; <see langword="null"/> means always on. <paramref name="isInternal"/> marks it internal-only
    /// (AC-204): hidden from every user-facing MCP selection and the no-selection fan-out, yet still mountable when a
    /// launch names it explicitly — for an endpoint only a specific spawn should mount (the Autopilot CEO/step tools).
    /// <paramref name="alwaysMounted"/> is the opposite arrangement: hidden from the pickers too, but mounted into
    /// every session whatever was selected — for cockpit plumbing that is not a choice (<c>cockpit-session</c>).
    /// </summary>
    Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, bool alwaysMounted = false, CancellationToken cancellationToken = default);
}
