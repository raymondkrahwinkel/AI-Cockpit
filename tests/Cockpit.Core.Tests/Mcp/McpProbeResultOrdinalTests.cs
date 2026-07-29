using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The plugin-facing enums' own ordinal guarantee (AC-503) — the same lesson AC-243 already recorded in
/// BuildTraps.md for <c>PluginMcpSignInOutcome</c>: an unstubbed <c>Substitute.For&lt;ICockpitHost&gt;()</c>, or any
/// other unconfigured <c>Task&lt;T&gt;</c> fake, must land on the safest reading of "nothing was confirmed" —
/// never "confirmed", and for <see cref="ProjectMemorySourceReachability"/> specifically, never the more assertive
/// "not found" either.
/// </summary>
public class McpProbeResultOrdinalTests
{
    [Fact]
    public void McpProbeOutcome_FailedIsOrdinalZero() =>
        Assert.Equal(0, (int)McpProbeOutcome.Failed);

    [Fact]
    public void McpProbeOutcome_DefaultIsFailed() =>
        Assert.Equal(McpProbeOutcome.Failed, default);

    [Fact]
    public void ProjectMemorySourceReachability_NotSignedInIsOrdinalZero() =>
        Assert.Equal(0, (int)ProjectMemorySourceReachability.NotSignedIn);

    [Fact]
    public void ProjectMemorySourceReachability_DefaultIsNotSignedIn() =>
        Assert.Equal(ProjectMemorySourceReachability.NotSignedIn, default);

    [Fact]
    public void McpProbeResult_FailedFactory_NeverCarriesADetail() =>
        Assert.Null(McpProbeResult.Failed.Detail);

    [Fact]
    public void McpProbeResult_NotSignedInFactory_NeverCarriesADetail() =>
        Assert.Null(McpProbeResult.NotSignedIn.Detail);

    [Fact]
    public void McpProbeResult_NotFoundFactory_NeverCarriesADetail() =>
        Assert.Null(McpProbeResult.NotFound.Detail);
}
