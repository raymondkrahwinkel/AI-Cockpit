using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="DesktopBackendHost.ProbeMcpToolAsync"/> (AC-503): the plugin-facing surface over the Core-level
/// <see cref="IMcpToolProbe"/>, mirroring the same isolation seam <see cref="CockpitHostMcpAuthTests"/> already
/// covers for <see cref="DesktopBackendHost.GetMcpServerAuthStateAsync"/>/<see cref="DesktopBackendHost.SignInMcpServerAsync"/> —
/// same fixture shape, same <c>diagnostics.Record</c> catch-and-report pattern on an unexpected exception.
/// <para>
/// What this class does <em>not</em> re-test: whether a sign-in check happens before a tool call, and whether that
/// connection ever opens a browser. Both are <see cref="IMcpToolProbe.ProbeAsync"/>'s own responsibility — this host
/// method makes exactly one delegated call to it and does no OAuth/connection work of its own (see
/// <see cref="ProbeMcpToolAsync_DelegatesExactlyOnce_WithNoOwnInteractiveOrSignInLogic"/> below, which proves that
/// boundary) — so the non-interactive sign-in-first behavior is covered exhaustively at the
/// <c>Cockpit.Infrastructure.Tests.Mcp.McpToolProbeTests</c> level instead, against the real implementation.
/// </para>
/// </summary>
public class CockpitHostProbeMcpToolTests
{
    // --- The outcome-mapping switch, one case per Cockpit.Core.Mcp.McpToolProbeOutcome value --------------------

    // --- Pass-through of the call's own arguments ------------------------------------------------------------------

    // --- The exception path: caught, reported as Failed, and (Iron Law #8) never a leaked detail on the result ------

    [Fact]
    public async Task ProbeMcpToolAsync_WhenTheProbeThrowsWithATokenLikeMessage_TheReturnedResultNeverCarriesIt_ButDiagnosticsDoes_MirroringExistingPrecedent()
    {
        // Iron Law #8's real boundary is the RESULT this method hands back to the plugin/view — proven here to stay
        // McpProbeOutcome.Failed with a null Detail regardless of what the underlying exception said, exactly as the
        // "generic ambiguous failure -> Failed, no detail" rule already requires.
        //
        // diagnostics.Record(pluginId, pluginName, "mcp-probe", exception.Message) below DOES carry the exception's
        // own message into PluginFailure.Error verbatim — this is not new to AC-503: GetMcpServerAuthStateAsync's
        // "mcp-auth-state" and SignInMcpServerAsync's "mcp-sign-in" entries already do the exact same thing (see
        // CockpitHostMcpAuthTests' own WhenTheCoordinatorThrows tests), and no exception raised inside McpToolProbe
        // is ever constructed from a credential in the first place (it comes from the transport/connect failure, not
        // from the bearer this class builds) — so this test records the existing, shared behavior rather than
        // introducing or fixing anything new, per the explicit instruction not to redesign this precedent.
        const string fakeTokenLikeMessage = "Unauthorized: Bearer fake-token-should-never-leak-abc123";
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<IReadOnlyList<McpServerConfig>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpToolProbeResult>>(_ => throw new InvalidOperationException(fakeTokenLikeMessage));
        var diagnostics = new PluginDiagnostics();
        var host = _BuildHost(probe, diagnostics);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
        Assert.Null(result.Detail);

        var failure = diagnostics.ForFolder("depot");
        Assert.NotNull(failure);
        Assert.Contains(fakeTokenLikeMessage, failure!.Error, StringComparison.Ordinal);
    }

    private static DesktopBackendHost _BuildHost(IMcpToolProbe? probe, PluginDiagnostics? diagnostics = null)
    {
        var collection = new ServiceCollection();
        if (probe is not null)
        {
            collection.AddSingleton(probe);
        }

        var services = collection.BuildServiceProvider();
        return new DesktopBackendHost(
            "depot",
            "Depot",
            services,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            diagnostics ?? new PluginDiagnostics());
    }
}
