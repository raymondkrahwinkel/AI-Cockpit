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
/// AC-499: <see cref="CockpitHost.CallMcpToolAsync"/> hands <see cref="IMcpToolInvoker.InvokeAsync"/> a
/// caller-scoped fallback candidate list (<c>callerFallbackServers</c>) built from this plugin's <em>own</em>
/// <see cref="IPluginMcpProvider"/> contributions only — never another plugin's, even though those live in the
/// same container-wide <c>services.GetServices&lt;IPluginMcpProvider&gt;()</c> set and even though the separate
/// accept check (<c>_IsKnownMcpServerNameAsync</c>, <see cref="CockpitHostCallMcpToolTests"/>) stays deliberately
/// unscoped (pre-existing AC-504 behaviour, not narrowed here). Two providers of distinct concrete types stand in
/// for "this plugin" and "some other plugin" — the host is told which one is its own via the plugin's runtime
/// type (<c>ownPluginType</c>, wired in production through <c>PluginManager.Initialize</c> → <c>App.axaml.cs</c>).
/// </summary>
public class CockpitHostCallMcpToolFallbackScopingTests
{
    [Fact]
    public async Task TheFallbackHandedToTheInvoker_ContainsOnlyThisPluginsOwnContribution_NeverAnotherPluginsProvider()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        IReadOnlyList<McpServerConfig>? captured = null;
        invoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(),
                Arg.Do<IReadOnlyList<McpServerConfig>?>(list => captured = list), Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success("ok"));
        var own = new _FakeMcpProviderA([new McpServerContribution("Own: Server", "https://own.example.com/mcp")]);
        var other = new _FakeMcpProviderB([new McpServerContribution("Other: Server", "https://other.example.com/mcp")]);
        var host = _BuildHost(invoker, [own, other], ownPluginType: typeof(_FakeMcpProviderA));

        var result = await host.CallMcpToolAsync("Own: Server", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.NotNull(captured);
        var fallbackNames = captured.Select(config => config.Name).ToList();
        Assert.Contains("Own: Server", fallbackNames);
        Assert.DoesNotContain("Other: Server", fallbackNames);
    }

    [Fact]
    public async Task NoOwnPluginTypeSupplied_TheFallbackIsEmpty()
    {
        // Every existing CockpitHost test that never passes ownPluginType (the vast majority) must keep behaving
        // exactly as before this fix — an empty fallback, same as if AC-499 had never touched this path.
        var invoker = Substitute.For<IMcpToolInvoker>();
        IReadOnlyList<McpServerConfig>? captured = null;
        invoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(),
                Arg.Do<IReadOnlyList<McpServerConfig>?>(list => captured = list), Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success("ok"));
        var own = new _FakeMcpProviderA([new McpServerContribution("Own: Server", "https://own.example.com/mcp")]);
        var host = _BuildHost(invoker, [own], ownPluginType: null);

        await host.CallMcpToolAsync("Own: Server", "list_projects");

        Assert.NotNull(captured);
        Assert.Empty(captured);
    }

    // --- The important one: an unentitled caller must still be refused ---------------------------------------------

    [Fact]
    public async Task ANameKnownOnlyThroughAnotherPluginsProvider_StillFailsToResolve_EvenThoughTheAcceptCheckLetItThrough()
    {
        // _IsKnownMcpServerNameAsync (the accept check right before this) is unscoped by design (AC-504) — it lets
        // "Other: Server" through purely because SOME provider in the shared container knows the name. If the
        // security boundary lived there, this call would already have failed before ever reaching an invoker. It
        // does not: the boundary is what CallMcpToolAsync hands the invoker as callerFallbackServers. Wired against
        // the REAL McpToolProvider (not a mock standing in for "trust me, it refuses") with an empty catalog, so
        // the refusal is the actual resolution failing to find the name anywhere it was allowed to look — not an
        // assertion about an argument this test merely expected to see.
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var realInvoker = new McpToolProvider(
            catalog,
            Substitute.For<IMcpOAuthAuthorizer>(),
            Substitute.For<IMcpOAuthCoordinator>(),
            new McpAuthKey(),
            new SessionMcpKeyring(),
            NullLogger<McpToolProvider>.Instance);
        var own = new _FakeMcpProviderA([new McpServerContribution("Own: Server", "https://own.example.com/mcp")]);
        var other = new _FakeMcpProviderB([new McpServerContribution("Other: Server", "https://other.example.com/mcp")]);
        var host = _BuildHost(realInvoker, [own, other], ownPluginType: typeof(_FakeMcpProviderA));

        // The accept check does let this through — proven so this test cannot be trivially "passing" because
        // CallMcpToolAsync bailed out before ever asking the invoker anything.
        var accepted = await host.CallMcpToolAsync("Other: Server", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, accepted.Outcome);
        // The exact "no enabled server" message, not merely some failure: it proves the name was never found —
        // and so never even attempted a connection to the other plugin's URL — rather than the different message
        // ("Could not connect to …") a scoping bug that let the other plugin's server slip into the fallback list
        // would produce instead.
        Assert.Equal("No enabled MCP server named \"Other: Server\".", accepted.Error);
    }

    private static CockpitHost _BuildHost(IMcpToolInvoker invoker, IReadOnlyList<IPluginMcpProvider> providers, Type? ownPluginType)
    {
        var collection = new ServiceCollection().AddSingleton(invoker);
        foreach (var provider in providers)
        {
            collection.AddSingleton(provider);
        }

        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        collection.AddSingleton(store);

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

    // Two distinct concrete types standing in for "this plugin's own IPluginMcpProvider" and "some other plugin's" —
    // the exact shape the real Depot plugin uses (services.AddSingleton<IPluginMcpProvider>(this)), which is why
    // CockpitHost's own scoping matches by the provider's concrete runtime type.
    private sealed class _FakeMcpProviderA(IReadOnlyList<McpServerContribution> servers) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => servers;
    }

    private sealed class _FakeMcpProviderB(IReadOnlyList<McpServerContribution> servers) : IPluginMcpProvider
    {
        public IReadOnlyList<McpServerContribution> GetMcpServers() => servers;
    }
}
