using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The MCP OAuth status surfaced in two places (AC-355): the MCP-servers dialog (per-row status, sign-in/sign-out)
/// and the New-session checklist (a specific "needs sign-in" tooltip, and a non-blocking start-time notice). Both
/// read <see cref="IMcpOAuthCoordinator"/>, which this file substitutes — a public interface, so NSubstitute rather
/// than a hand-written fake.
/// </summary>
public class McpAuthStatusTests
{
    private static McpServerConfig _OAuthServer(string name = "gh") => new()
    {
        Name = name,
        Transport = McpTransport.Http,
        Url = "https://x/mcp",
        Auth = McpServerAuth.OAuth,
    };

    // --- MCP-servers dialog: per-row status + sign-in/sign-out ---

    [Fact]
    public async Task SignOut_WithdrawsAccess_AndRefreshesTheStatus()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new[] { _OAuthServer() });
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>())
            .Returns(McpAuthState.Authorized, McpAuthState.AuthorizationRequired);
        var vm = new McpServersViewModel(store, [], coordinator);
        await vm.LoadAsync();
        var row = vm.Servers.Single();
        Assert.Equal(McpAuthState.Authorized, row.AuthState);

        await row.SignOutCommand.ExecuteAsync(null);

        await coordinator.Received(1).SignOutAsync(Arg.Is<McpServerConfig>(s => s.Name == "gh"), Arg.Any<CancellationToken>());
        Assert.Equal(McpAuthState.AuthorizationRequired, row.AuthState);
    }

    // --- New-session checklist: specific tooltip + non-blocking start-time notice ---

    [Fact]
    public async Task SessionStartNotice_AppearsForASelectedUnauthorizedServer_AndDoesNotBlockStart()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { profile });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        var mcpServerCatalog = Substitute.For<IMcpServerCatalog>();
        var registry = new[] { _OAuthServer("gh") };
        mcpServerCatalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        mcpServerCatalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());

        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>())
            .Returns(McpAuthState.AuthorizationRequired);

        var vm = new NewSessionDialogViewModel(
            store, loginChecker, mcpServerCatalog, workingPathStore: null, plugins: null,
            ttyProviderResolver: null,
            worktreeManager: null, tokenEstimator: null, projectStore: null, oauthCoordinator: coordinator);

        await vm.LoadAsync();

        Assert.True(vm.ShowMcpAuthorizationHint);
        Assert.Contains("gh", vm.McpAuthorizationHintText);

        // Advisory only — Start must still be reachable for a selected server needing a sign-in.
        Assert.True(vm.CanStart);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
    }
}
