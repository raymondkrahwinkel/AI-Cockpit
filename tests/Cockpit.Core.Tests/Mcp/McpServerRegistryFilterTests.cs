using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpServerRegistryFilter.ApplySessionSelection"/>: the per-session MCP-server selection (#44)
/// narrows the registry to the given names, and a <see langword="null"/> selection is a no-op pass-through.
/// </summary>
public class McpServerRegistryFilterTests
{
    private static readonly McpServerConfig ServerA = new() { Name = "server-a", Command = "npx" };
    private static readonly McpServerConfig ServerB = new() { Name = "server-b", Command = "npx" };

    // An internal-only endpoint (AC-204, the Autopilot CEO/step tools): hosted and mountable, but hidden from every
    // user-facing selection and the no-selection fan-out.
    private static readonly McpServerConfig InternalServer = new() { Name = "cockpit-autopilot-ceo", Url = "http://127.0.0.1:1/mcp", Internal = true };

    [Fact]
    public void ApplySessionSelection_WithNullSelection_DropsInternalEndpoints_FromTheAllEnabledFanOut()
    {
        // No selection means "every enabled server", but an internal-only endpoint (AC-204) must never fan into a
        // session that did not name it — an unrelated no-selection session started while an Autopilot run is live
        // must not inherit the CEO/step tools. Red without the fix, which returned the registry verbatim here.
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, InternalServer, ServerB], enabledServerNames: null);

        Assert.Equal(new[] { ServerA, ServerB }, result);
    }

    // ── WithAutoMountedServers (AC-869): folding an internal endpoint's own mount rule into a selection ──────────

}
