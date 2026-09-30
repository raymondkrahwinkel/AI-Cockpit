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
/// <see cref="DesktopBackendHost.CallMcpToolAsync"/> (AC-502) — the plugin-facing bridge onto the app's own
/// <see cref="IMcpToolInvoker"/>, added so a plugin (the project editor's Depot picker) can call a tool on its own
/// contributed MCP server before any session exists, without ever seeing the bearer token the invoker used.
/// <para>
/// Review fix: scoped by <see cref="DesktopBackendHost._IsKnownMcpServerNameAsync"/> — the shared registry (any
/// <see cref="AddMcpServer"/> caller ends up here) or any plugin's own <see cref="IPluginMcpProvider.GetMcpServers()"/>
/// (AC-504's per-project delivery model, Depot's own since that ticket). Every test below registers its server
/// through a stateful <see cref="IMcpServerStore"/> fake, so <see cref="ICockpitHost.AddMcpServer"/> and
/// <see cref="ICockpitHost.RemoveMcpServer"/> actually change what a later <see cref="CallMcpToolAsync"/> sees,
/// the same way a real store would.
/// </para>
/// </summary>
public class CockpitHostCallMcpToolTests
{
    [Fact]
    public async Task AServerNeverRegisteredAnywhere_AnswersUnavailable_WithoutAskingTheInvoker()
    {
        // The security-relevant case: an unrelated server name (a cockpit-internal endpoint, say) must never be
        // reachable through this bridge just because it happens to be enabled somewhere in the shared catalog.
        var invoker = Substitute.For<IMcpToolInvoker>();
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);

        var result = await host.CallMcpToolAsync("cockpit-terminal", "run_command");

        Assert.Equal(PluginMcpToolCallOutcome.Unavailable, result.Outcome);
        await invoker.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<IReadOnlyList<McpServerConfig>?>(), Arg.Any<CancellationToken>());
    }

    private static async Task<DesktopBackendHost> _BuildHostWithRegisteredServerAsync(
        string? serverName, IMcpToolInvoker? invoker, PluginDiagnostics? diagnostics = null, IPluginMcpProvider? mcpProvider = null)
    {
        var collection = new ServiceCollection();
        if (invoker is not null)
        {
            collection.AddSingleton(invoker);
        }

        if (mcpProvider is not null)
        {
            collection.AddSingleton(mcpProvider);
        }

        // A stateful fake, not a fixed .Returns([]) — AddMcpServer/RemoveMcpServer below have to actually change
        // what a later LoadAsync sees, the same as a real store, or the "removed" test would trivially pass for
        // the wrong reason (nothing was ever really there to remove).
        var servers = new List<McpServerConfig>();
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => servers.ToList());
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                servers = callInfo.Arg<IReadOnlyList<McpServerConfig>>().ToList();
                return Task.CompletedTask;
            });
        collection.AddSingleton(store);

        var services = collection.BuildServiceProvider();
        var host = new DesktopBackendHost(
            "depot",
            "Depot",
            services,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            diagnostics ?? new PluginDiagnostics());

        if (serverName is not null)
        {
            await host.AddMcpServer(new McpServerContribution(serverName, "https://depot.example.com/mcp"));
        }

        return host;
    }
}
