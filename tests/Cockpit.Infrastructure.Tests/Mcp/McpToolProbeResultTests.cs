using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// <see cref="McpToolProbeOutcome"/>'s own ordinal guarantee (AC-503, mirroring the AC-243 lesson recorded in
/// BuildTraps.md: <c>PluginMcpSignInOutcome.Authorized</c> used to sit at ordinal 0, so an unconfigured
/// <c>Substitute.For&lt;ICockpitHost&gt;()</c> reported "signed in" for free). <c>Failed</c> must be the default —
/// never <c>Success</c>, and never the more specific <c>NotFound</c>.
/// </summary>
public class McpToolProbeResultTests
{
    [Fact]
    public void Failed_IsOrdinalZero_SoAnUnstubbedFakeNeverReadsAsSuccessOrNotFound() =>
        Assert.Equal(0, (int)McpToolProbeOutcome.Failed);

}
