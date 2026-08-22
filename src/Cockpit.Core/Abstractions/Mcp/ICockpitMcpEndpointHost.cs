namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Hosts cockpit MCP endpoints (#AC-13, #AC-12). A plugin can also mount one at runtime via
/// <see cref="MountAsync"/> for a key-guarded loopback MCP server answered live to the fan-out, not the registry
/// (AC-40) — this reference is how the App's <c>ICockpitHost</c> reaches the Infrastructure host.
/// </summary>
public interface ICockpitMcpEndpointHost
{
    /// <summary>
    /// Mounts an MCP endpoint for <paramref name="tools"/> on a loopback address under <paramref name="serverName"/>
    /// — the cockpit's own, not the registry (AC-40), idempotent per name. <paramref name="isEnabled"/> gates it on
    /// the plugin's setting (null = always on); <paramref name="isInternal"/> (AC-204) hides it but keeps it explicitly mountable; <paramref name="alwaysMounted"/> mounts into every session regardless.
    /// </summary>
    Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, bool alwaysMounted = false, CancellationToken cancellationToken = default);
}
