using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.ProbeMcpToolAsync"/> (AC-503): the plugin-facing surface over the Core-level
/// <see cref="IMcpToolProbe"/>, mirroring the same isolation seam <see cref="CockpitHostMcpAuthTests"/> already
/// covers for <see cref="CockpitHost.GetMcpServerAuthStateAsync"/>/<see cref="CockpitHost.SignInMcpServerAsync"/> —
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
    [Fact]
    public async Task ProbeMcpToolAsync_NoProbeRegistered_AnswersFailed_WithoutAttemptingAnything()
    {
        var host = _BuildHost(probe: null);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
        Assert.Null(result.Detail);
    }

    // --- The outcome-mapping switch, one case per Cockpit.Core.Mcp.McpToolProbeOutcome value --------------------

    [Fact]
    public async Task ProbeMcpToolAsync_CoreOutcomeNotSignedIn_MapsToThePluginFacingNotSignedIn()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.NotSignedIn);
        var host = _BuildHost(probe);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.NotSignedIn, result.Outcome);
    }

    [Fact]
    public async Task ProbeMcpToolAsync_CoreOutcomeNotFound_MapsToThePluginFacingNotFound()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.NotFound);
        var host = _BuildHost(probe);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ProbeMcpToolAsync_CoreOutcomeSuccess_MapsToThePluginFacingSuccess_CarryingTheDetailThrough()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.Success("24 documents, last changed 2 hours ago"));
        var host = _BuildHost(probe);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Success, result.Outcome);
        Assert.Equal("24 documents, last changed 2 hours ago", result.Detail);
    }

    [Fact]
    public async Task ProbeMcpToolAsync_CoreOutcomeFailed_MapsToThePluginFacingFailed()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.Failed);
        var host = _BuildHost(probe);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ProbeMcpToolAsync_AnUnrecognisedCoreOutcome_FallsBackToFailed_NeverToSuccessOrNotFound()
    {
        // Defends the switch's own "_ => Failed" arm directly: a Core-level outcome this switch does not name (a
        // future addition to McpToolProbeOutcome the mapping was not updated for) must land on the safest reading,
        // the same ordinal-zero guarantee McpToolProbeOutcomeTests already pins for the type itself.
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(new McpToolProbeResult((McpToolProbeOutcome)99));
        var host = _BuildHost(probe);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
    }

    // --- Pass-through of the call's own arguments ------------------------------------------------------------------

    [Fact]
    public async Task ProbeMcpToolAsync_DelegatesExactlyOnce_WithNoOwnInteractiveOrSignInLogic()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.Success(null));
        var host = _BuildHost(probe);
        var arguments = new Dictionary<string, object?> { ["project"] = "cockpit" };
        using var cts = new CancellationTokenSource();

        await host.ProbeMcpToolAsync("Depot: Work", "outline", arguments, cts.Token);

        // Exactly one call, with the same server name, tool name, argument dictionary reference and cancellation
        // token this method itself was given — this host method does no OAuth work, opens no connection, and asks
        // nothing else; every one of those responsibilities belongs to the IMcpToolProbe implementation, which is
        // where the non-interactive sign-in-first guarantee is actually proven (McpToolProbeTests, Infrastructure).
        await probe.Received(1).ProbeAsync("Depot: Work", "outline", arguments, cts.Token);
    }

    // --- The exception path: caught, reported as Failed, and (Iron Law #8) never a leaked detail on the result ------

    [Fact]
    public async Task ProbeMcpToolAsync_WhenTheProbeThrows_AnswersFailed_AndRecordsAFailure()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpToolProbeResult>>(_ => throw new InvalidOperationException("connection refused"));
        var diagnostics = new PluginDiagnostics();
        var host = _BuildHost(probe, diagnostics);

        var result = await host.ProbeMcpToolAsync("Depot: Work", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
        Assert.Null(result.Detail);
        var failure = diagnostics.ForFolder("depot");
        Assert.NotNull(failure);
        Assert.Equal("mcp-probe", failure!.Phase);
    }

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
        probe.ProbeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<CancellationToken>())
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

    private static CockpitHost _BuildHost(IMcpToolProbe? probe, PluginDiagnostics? diagnostics = null)
    {
        var collection = new ServiceCollection();
        if (probe is not null)
        {
            collection.AddSingleton(probe);
        }

        var services = collection.BuildServiceProvider();
        return new CockpitHost(
            "depot",
            "Depot",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            diagnostics ?? new PluginDiagnostics());
    }
}
