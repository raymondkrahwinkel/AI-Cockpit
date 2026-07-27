using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The three ways the sign-in surface got its own identity wrong (AC-355), each found by review rather than by a
/// green suite: a dialog that would not open, a withdrawal that withdrew nothing, and a badge outliving its reason.
/// </summary>
public class McpAuthStatusRegressionTests
{
    private static McpServerConfig _OAuthServer(string name = "depot", string url = "https://depot.example/mcp") => new()
    {
        Name = name,
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
    };

    [Fact]
    public async Task SignOut_AfterRenamingWithoutSaving_WithdrawsUnderTheNameTheStoreKnows()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();

        editable.Name = "depot-renamed";
        await editable.SignOutCommand.ExecuteAsync(null);

        // A token is filed under the server's name. Withdrawing under the newly typed one removed nothing while
        // telling the operator their access was gone — the bearer stayed in cockpit.json behind a reassuring badge.
        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_AfterEditingTheUrl_AuthorizesAgainstWhatIsTypedButUnderTheStoredName()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);

        editable.Url = "https://depot.example/mcp/v2";
        await editable.SignInCommand.ExecuteAsync(null);

        // Both halves matter: correcting an address and then signing in must go against the corrected one, while the
        // name stays the key everything else looks the token up by.
        await coordinator.Received().AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot" && server.Url == "https://depot.example/mcp/v2"),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditingTheUrl_ClearsAStandingSignedInBadge()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();
        Assert.True(editable.ShowAuthStatus);

        editable.Url = "https://somewhere-else.example/mcp";

        // A held token is bound to the host it was obtained for, so retyping the address can make "signed in" false
        // with nothing else changing. A label whose reason has gone is the failure this project has met before.
        Assert.False(editable.ShowAuthStatus);
    }

    [Fact]
    public async Task NewSessionDialog_OpensEvenWhenTwoServersShareAName()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var store = Substitute.For<ISessionProfileStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { profile });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        // "Add server" twice leaves two rows called "new server", and nothing between there and the store objects.
        var registry = new[] { _OAuthServer("new server"), _OAuthServer("new server", "https://other.example/mcp") };
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());

        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);

        var viewModel = new NewSessionDialogViewModel(
            store, loginChecker, catalog, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver: null, ttyProviderRegistry: null, sessionProviderRegistry: null,
            worktreeManager: null, tokenEstimator: null, projectStore: null, oauthCoordinator: coordinator);

        // Pairing the rows to the registry by name threw here, outside any catch, so the dialog did not open at all.
        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.McpServers.Count);
    }

    [Fact]
    public async Task SignInFailure_ShowsAFixedLine_AndNeverTheToken()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<McpOAuthAccess>(_ => throw new InvalidOperationException("boom: token=super-secret-value"));
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);

        await editable.SignInCommand.ExecuteAsync(null);

        // Iron Law #8. An OAuth failure can carry request or response material, so the operator gets our own words
        // rather than whatever the exception happened to say.
        Assert.NotEmpty(editable.AuthMessage);
        Assert.DoesNotContain("super-secret-value", editable.AuthMessage);
        Assert.DoesNotContain("boom", editable.AuthMessage);
    }
}
