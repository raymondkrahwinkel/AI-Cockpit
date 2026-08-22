using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Makes one MCP tool call outside any running session (AC-503) — what a plugin's
/// <c>ICockpitHost.ProbeMcpToolAsync</c> runs against via the app layer, mapping this interface's
/// <see cref="McpToolProbeResult"/> onto the plugin-facing one. Never asks interactively: a server needing a sign-in reports <see cref="McpToolProbeOutcome.NotSignedIn"/> without opening a browser.
/// </summary>
public interface IMcpToolProbe
{
    /// <summary>
    /// Calls <paramref name="toolName"/> on server <paramref name="serverName"/>, short-lived, resolved against the
    /// registry and, only if nothing is found, against <paramref name="callerFallbackServers"/> (AC-499) — servers
    /// delivered per-project (Depot, AC-504), since this call takes no project id. An unknown name or unsigned-in OAuth server answers <see cref="McpToolProbeOutcome.Failed"/> / <see cref="McpToolProbeOutcome.NotSignedIn"/> untried.
    /// </summary>
    Task<McpToolProbeResult> ProbeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        IReadOnlyList<McpServerConfig>? callerFallbackServers = null,
        CancellationToken cancellationToken = default);
}
