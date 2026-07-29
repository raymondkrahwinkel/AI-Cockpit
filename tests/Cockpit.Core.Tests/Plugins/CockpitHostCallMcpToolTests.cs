using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.CallMcpToolAsync"/> (AC-502) — the plugin-facing bridge onto the app's own
/// <see cref="IMcpToolInvoker"/>, added so a plugin (the project editor's Depot picker) can call a tool on its own
/// contributed MCP server before any session exists, without ever seeing the bearer token the invoker used.
/// <para>
/// Review fix: scoped to servers <em>this</em> plugin itself registered via <see cref="ICockpitHost.AddMcpServer"/>
/// — <see cref="IMcpToolInvoker.InvokeAsync"/> resolves against the same merged catalog a session connects to,
/// which also holds every other plugin's contributions and every cockpit-internal endpoint, none of which carry
/// this plugin's own consent. Every test below registers its server through the real <c>AddMcpServer</c> call
/// first, the same way a plugin would, rather than reaching into the host's private tracking directly.
/// </para>
/// </summary>
public class CockpitHostCallMcpToolTests
{
    [Fact]
    public async Task NoInvokerRegistered_AnswersUnavailable()
    {
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker: null);

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task AServerThisPluginNeverRegistered_AnswersUnavailable_WithoutAskingTheInvoker()
    {
        // The security-relevant case: an unrelated server name (another plugin's, an operator-configured one, a
        // cockpit-internal endpoint) must never be reachable through this bridge just because it happens to be
        // enabled somewhere in the shared catalog.
        var invoker = Substitute.For<IMcpToolInvoker>();
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);

        var result = await host.CallMcpToolAsync("cockpit-terminal", "run_command");

        Assert.Equal(PluginMcpToolCallOutcome.Unavailable, result.Outcome);
        await invoker.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARemovedServer_AnswersUnavailable()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);
        await host.RemoveMcpServer("Depot: Work");

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task InvokerSucceeds_CarriesTheContentThroughVerbatim()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync("Depot: Work", "list_projects", Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Success("""{"projects":[]}"""));
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Success, result.Outcome);
        Assert.Equal("""{"projects":[]}""", result.Content);
    }

    [Fact]
    public async Task InvokerReportsAuthorizationRequired_IsCarriedThroughVerbatim()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.AuthorizationRequired);
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.AuthorizationRequired, result.Outcome);
    }

    [Fact]
    public async Task InvokerFails_CarriesTheErrorThroughVerbatim()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(McpToolInvocationResult.Failed("no such server"));
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker);

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        Assert.Equal("no such server", result.Error);
    }

    [Fact]
    public async Task WhenTheInvokerThrows_AnswersFailed_AndRecordsAFailure_RatherThanCrashing()
    {
        var invoker = Substitute.For<IMcpToolInvoker>();
        invoker.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpToolInvocationResult>>(_ => throw new InvalidOperationException("connect blew up"));
        var diagnostics = new PluginDiagnostics();
        var host = await _BuildHostWithRegisteredServerAsync("Depot: Work", invoker, diagnostics);

        var result = await host.CallMcpToolAsync("Depot: Work", "list_projects");

        Assert.Equal(PluginMcpToolCallOutcome.Failed, result.Outcome);
        var failure = diagnostics.ForFolder("depot");
        Assert.NotNull(failure);
        Assert.Equal("mcp-tool-call", failure!.Phase);
    }

    private static async Task<CockpitHost> _BuildHostWithRegisteredServerAsync(string serverName, IMcpToolInvoker? invoker, PluginDiagnostics? diagnostics = null)
    {
        var collection = new ServiceCollection();
        if (invoker is not null)
        {
            collection.AddSingleton(invoker);
        }

        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns([]);
        collection.AddSingleton(store);

        var services = collection.BuildServiceProvider();
        var host = new CockpitHost(
            "depot",
            "Depot",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            diagnostics ?? new PluginDiagnostics());

        await host.AddMcpServer(new McpServerContribution(serverName, "https://depot.example.com/mcp"));
        return host;
    }
}
