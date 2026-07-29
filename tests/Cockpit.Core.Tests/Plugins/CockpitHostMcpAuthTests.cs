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
/// <see cref="CockpitHost.GetMcpServerAuthStateAsync"/> and <see cref="CockpitHost.SignInMcpServerAsync"/> (AC-243):
/// the plugin-facing read/act surface over the shared <see cref="IMcpOAuthCoordinator"/>, added because a plugin
/// that contributes an OAuth <see cref="McpServerContribution"/> (AC-500) had no way to learn or drive its own
/// sign-in standing — <see cref="CockpitHost.AddMcpServer"/> is fire-and-forget with no read path back. Both members
/// look the contribution up by name in the shared <see cref="IMcpServerStore"/> first, so a name with no OAuth entry
/// — never contributed, contributed as a static token, removed, or simply misspelled — answers "nothing to report"
/// rather than acting on a server that is not this plugin's.
/// </summary>
public class CockpitHostMcpAuthTests
{
    private static readonly McpServerConfig OAuthServer = new()
    {
        Name = "Depot: Work",
        Transport = McpTransport.Http,
        Url = "https://depot.example.com/mcp",
        Auth = McpServerAuth.OAuth,
        OAuthAuthority = "https://depot.example.com",
    };

    [Fact]
    public async Task GetMcpServerAuthStateAsync_NoCoordinatorRegistered_AnswersUnknown()
    {
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator: null);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_NoServerOfThatName_AnswersUnknown_WithoutAskingTheCoordinator()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Nonexistent");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
        await coordinator.DidNotReceive().GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
    }

    // The guard that keeps this from acting on a server that used to be OAuth, or was never this plugin's: a
    // same-named entry that is not OAuth must not be handed to the coordinator as though it were.
    [Fact]
    public async Task GetMcpServerAuthStateAsync_SameNameButNotOAuth_AnswersUnknown_WithoutAskingTheCoordinator()
    {
        var staticServer = OAuthServer with { Auth = McpServerAuth.ApiKey, OAuthAuthority = null };
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var host = _BuildHost(new List<McpServerConfig> { staticServer }, coordinator);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
        await coordinator.DidNotReceive().GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_CoordinatorReportsAuthorized_IsCarriedThroughVerbatim()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Authorized, state);
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_CoordinatorReportsAuthorizationRequired_IsCarriedThroughVerbatim()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.AuthorizationRequired);
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.AuthorizationRequired, state);
    }

    [Fact]
    public async Task GetMcpServerAuthStateAsync_WhenTheCoordinatorThrows_AnswersUnknown_AndRecordsAFailure()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpAuthState>>(_ => throw new InvalidOperationException("store unreadable"));
        var diagnostics = new PluginDiagnostics();
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator, diagnostics);

        var state = await host.GetMcpServerAuthStateAsync("Depot: Work");

        Assert.Equal(PluginMcpAuthState.Unknown, state);
        var failure = diagnostics.ForFolder("depot");
        Assert.NotNull(failure);
        Assert.Equal("mcp-auth-state", failure!.Phase);
    }

    [Fact]
    public async Task SignInMcpServerAsync_NoCoordinatorRegistered_AnswersUnavailable()
    {
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator: null);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Unavailable, outcome);
    }

    [Fact]
    public async Task SignInMcpServerAsync_SameNameButNotOAuth_AnswersUnavailable_WithoutOpeningABrowser()
    {
        var staticServer = OAuthServer with { Auth = McpServerAuth.ApiKey, OAuthAuthority = null };
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var host = _BuildHost(new List<McpServerConfig> { staticServer }, coordinator);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Unavailable, outcome);
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInMcpServerAsync_AsksTheCoordinatorInteractively_NeverSilently()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpOAuthAccess(McpAuthState.Authorized, "token-abc", McpSignInStage.NoBrowserLaunched));
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        await host.SignInMcpServerAsync("Depot: Work");

        await coordinator.Received(1).AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "Depot: Work"),
            interactive: true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInMcpServerAsync_CoordinatorReturnsAuthorized_AnswersAuthorized()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpOAuthAccess(McpAuthState.Authorized, "token-abc", McpSignInStage.AuthorizationReturned));
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Authorized, outcome);
    }

    [Fact]
    public async Task SignInMcpServerAsync_CoordinatorReturnsAnythingElse_AnswersDeclined_NeverACredentialOrDetail()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new McpOAuthAccess(McpAuthState.AuthorizationRequired, null, McpSignInStage.BrowserRequested));
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Declined, outcome);
    }

    [Fact]
    public async Task SignInMcpServerAsync_WhenTheCoordinatorThrows_AnswersUnreachable_AndRecordsAFailure()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpOAuthAccess>>(_ => throw new InvalidOperationException("no network"));
        var diagnostics = new PluginDiagnostics();
        var host = _BuildHost(new List<McpServerConfig> { OAuthServer }, coordinator, diagnostics);

        var outcome = await host.SignInMcpServerAsync("Depot: Work");

        Assert.Equal(PluginMcpSignInOutcome.Unreachable, outcome);
        var failure = diagnostics.ForFolder("depot");
        Assert.NotNull(failure);
        Assert.Equal("mcp-sign-in", failure!.Phase);
    }

    private static CockpitHost _BuildHost(List<McpServerConfig> servers, IMcpOAuthCoordinator? coordinator, PluginDiagnostics? diagnostics = null)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(servers);

        var collection = new ServiceCollection().AddSingleton(store);
        if (coordinator is not null)
        {
            collection.AddSingleton(coordinator);
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
