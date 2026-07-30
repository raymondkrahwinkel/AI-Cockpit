using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-499, the same fallback-scoping story <see cref="CockpitHostCallMcpToolFallbackScopingTests"/> covers for
/// <see cref="CockpitHost.CallMcpToolAsync"/>, here for <see cref="CockpitHost.ProbeMcpToolAsync"/>: the caller-
/// scoped candidate list it hands <see cref="IMcpToolProbe.ProbeAsync"/> carries only this plugin's own
/// <see cref="IPluginMcpProvider"/> contribution, never another plugin's. <see cref="McpToolProbe.ProbeAsync"/>
/// took no project id and consulted only the shared registry before AC-499 — so a plugin whose servers never land
/// there (Depot, AC-504) could never be probed at all, regardless of any project's state; the important thing this
/// class proves is that closing that gap did not also open a "probe anyone's server" door.
/// </summary>
public class CockpitHostProbeMcpToolFallbackScopingTests
{
    [Fact]
    public async Task ANameKnownOnlyThroughAnotherPluginsProvider_StillFailsToResolve()
    {
        // Wired against the real McpToolProbe (not a mock) so the refusal is proven at the actual resolution step.
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var realProbe = new McpToolProbe(
            store,
            Substitute.For<IMcpOAuthCoordinator>(),
            Substitute.For<IMcpOAuthAuthorizer>(),
            new McpAuthKey(),
            NullLogger<McpToolProbe>.Instance);
        var own = new _FakeMcpProviderA([new McpServerContribution("Own: Server", "https://own.example.com/mcp")]);
        var other = new _FakeMcpProviderB([new McpServerContribution("Other: Server", "https://other.example.com/mcp")]);
        var host = _BuildHost(realProbe, [own, other], ownPluginType: typeof(_FakeMcpProviderA));

        var result = await host.ProbeMcpToolAsync("Other: Server", "outline");

        Assert.Equal(McpProbeOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task OwnPluginsServer_StillReachesTheProbeAsAFallbackCandidate()
    {
        var probe = Substitute.For<IMcpToolProbe>();
        IReadOnlyList<McpServerConfig>? captured = null;
        probe.ProbeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>>(),
                Arg.Do<IReadOnlyList<McpServerConfig>?>(list => captured = list), Arg.Any<CancellationToken>())
            .Returns(McpToolProbeResult.Success(null));
        var own = new _FakeMcpProviderA([new McpServerContribution("Own: Server", "https://own.example.com/mcp")]);
        var other = new _FakeMcpProviderB([new McpServerContribution("Other: Server", "https://other.example.com/mcp")]);
        var host = _BuildHost(probe, [own, other], ownPluginType: typeof(_FakeMcpProviderA));

        await host.ProbeMcpToolAsync("Own: Server", "outline");

        Assert.NotNull(captured);
        var fallbackNames = captured.Select(config => config.Name).ToList();
        Assert.Contains("Own: Server", fallbackNames);
        Assert.DoesNotContain("Other: Server", fallbackNames);
    }

    private static CockpitHost _BuildHost(IMcpToolProbe probe, IReadOnlyList<IPluginMcpProvider> providers, Type? ownPluginType)
    {
        var collection = new ServiceCollection().AddSingleton(probe);
        foreach (var provider in providers)
        {
            collection.AddSingleton(provider);
        }

        var services = collection.BuildServiceProvider();
        return new CockpitHost(
            "own",
            "Own Plugin",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics(),
            declaredSecretKeys: null,
            ownPluginType: ownPluginType);
    }

    private sealed class _FakeMcpProviderA(IReadOnlyList<McpServerContribution> servers) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => servers;
    }

    private sealed class _FakeMcpProviderB(IReadOnlyList<McpServerContribution> servers) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => servers;
    }
}
