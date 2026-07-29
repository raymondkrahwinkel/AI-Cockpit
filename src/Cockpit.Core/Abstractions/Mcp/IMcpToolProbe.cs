using Cockpit.Core.Mcp;

namespace Cockpit.Core.Abstractions.Mcp;

/// <summary>
/// Makes one MCP tool call outside any running session (AC-503) — what a plugin's <c>ICockpitHost.ProbeMcpToolAsync</c>
/// call actually runs against, via the app layer (<c>CockpitHost</c>), which maps this interface's Core-level
/// <see cref="McpToolProbeResult"/> onto the plugin-facing one. See <see cref="IMcpOAuthCoordinator"/>'s own remarks
/// on <em>interactive</em>: this never asks interactively — a server that needs a sign-in reports
/// <see cref="McpToolProbeOutcome.NotSignedIn"/> without ever opening a browser.
/// </summary>
public interface IMcpToolProbe
{
    /// <summary>
    /// Calls <paramref name="toolName"/> on the server named <paramref name="serverName"/> in the shared registry,
    /// short-lived and disposed immediately after. A server the registry does not know answers
    /// <see cref="McpToolProbeOutcome.Failed"/> without attempting anything (a caller passed a name nothing was ever
    /// registered under — not a claim about the value it was checking). A server whose auth is OAuth and not
    /// currently signed in answers <see cref="McpToolProbeOutcome.NotSignedIn"/>, also without attempting a
    /// connection. See <see cref="McpToolProbeResult"/>'s own remarks on why a network/timeout failure answers
    /// <see cref="McpToolProbeOutcome.Failed"/> rather than <see cref="McpToolProbeOutcome.NotFound"/>.
    /// </summary>
    Task<McpToolProbeResult> ProbeAsync(
        string serverName,
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default);
}
