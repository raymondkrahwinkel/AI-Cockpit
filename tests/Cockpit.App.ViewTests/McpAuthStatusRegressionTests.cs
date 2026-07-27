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
    public async Task SignIn_ForARowNeverSaved_UsesTheTypedName_NotThePlaceholderItWasCreatedWith()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));

        // What "Add server" produces: a placeholder name the operator is about to replace, and nothing in the store.
        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "new server", Command = "npx" }, coordinator, isPersisted: false);
        editable.Name = "depot";
        editable.Transport = McpTransport.Http;
        editable.Url = "https://depot.example/mcp";
        editable.Auth = McpServerAuth.OAuth;

        await editable.SignInCommand.ExecuteAsync(null);

        // Pinning to the placeholder files the token under a name that is about to be replaced: saving writes
        // "depot", the fan-out looks up "depot", and the bearer sits under "new server" behind a "signed in" badge.
        await coordinator.Received().AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_AfterRenamingAndEditingTheUrl_AuthorizesAgainstWhatIsTypedButUnderTheStoredName()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);

        editable.Name = "depot-renamed";
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
    public void AddServer_MintsANameNothingElseIsUsing()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var viewModel = new McpServersViewModel(store, []);

        viewModel.AddServerCommand.Execute(null);
        viewModel.AddServerCommand.Execute(null);

        // A name is a key downstream, not a label: a token is filed under it and each agent's config is keyed by it,
        // last one winning. Two rows called the same thing collapse into one mounted server while both sit ticked.
        Assert.Equal(2, viewModel.Servers.Select(server => server.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Save_RefusesTwoServersWithTheSameName()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "depot", Transport = McpTransport.Http, Url = "https://a.example/mcp" },
            new() { Name = "depot", Transport = McpTransport.Http, Url = "https://b.example/mcp" },
        });
        var viewModel = new McpServersViewModel(store, []);
        await viewModel.LoadAsync();

        await viewModel.SaveCommand.ExecuteAsync(null);

        // Refusing here is the last place it can still be said plainly — afterwards the duplicate is silent.
        Assert.Contains("depot", viewModel.StatusMessage);
        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuilding_AfterATickOnDuplicateNames_DoesNotThrow()
    {
        var profile = new SessionProfile("work", new ClaudeConfig("/home/r/.claude-work"));
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<SessionProfile> { profile });
        var loginChecker = Substitute.For<IProfileLoginChecker>();
        loginChecker.IsLoggedIn(profile).Returns(true);

        var registry = new[] { _OAuthServer("new server"), _OAuthServer("new server", "https://other.example/mcp") };
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns(registry.ToList());

        var viewModel = new NewSessionDialogViewModel(
            profileStore, loginChecker, catalog, workingPathStore: null, conversationPickers: null,
            ttyProviderResolver: null, ttyProviderRegistry: null, sessionProviderRegistry: null,
            worktreeManager: null, tokenEstimator: null, projectStore: null, oauthCoordinator: null);
        await viewModel.LoadAsync();

        // Ticking is what arms the second, separate name-keyed lookup: the one that carries the operator's own ticks
        // across a rebuild. It threw on a duplicate exactly like the first one, and only after a tick — which is why
        // a test that merely opens the dialog left it uncovered.
        viewModel.McpServers[0].IsEnabledForSession = false;
        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.McpServers.Count);
    }

    [Fact]
    public async Task SignOut_AfterRenamingARowThatSignedInWhileUnsaved_WithdrawsUnderTheNameItWasFiledBy()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);

        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "new server", Command = "npx" }, coordinator, isPersisted: false);
        editable.Name = "depot";
        editable.Transport = McpTransport.Http;
        editable.Url = "https://depot.example/mcp";
        editable.Auth = McpServerAuth.OAuth;
        await editable.SignInCommand.ExecuteAsync(null);

        editable.Name = "vault";
        await editable.SignOutCommand.ExecuteAsync(null);

        // Once a sign-in has filed the token, the row must stop following the name box. Letting it keep tracking sent
        // the withdrawal to a name nothing was filed under: the operator was told their access was gone while the
        // bearer stayed in cockpit.json. Same defect as the two rounds before, one case further along.
        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RenamingAServer_KeepsItsTokenWithdrawable()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();

        editable.Name = "vault";

        // Clearing the badge on a rename looks like the URL case and is not: the row still points at the token, and
        // the withdraw button is gated on the badge — hiding it would strand a credential the operator can no longer
        // reach. What a rename actually breaks is the save, which is a storage question and its own ticket.
        Assert.True(editable.ShowAuthStatus);
        Assert.True(editable.SignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_RefusesAServerNamedAfterOneTheCockpitRuns()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "mine", Transport = McpTransport.Http, Url = "https://mine.example/mcp" },
        });

        var internalProvider = Substitute.For<ICockpitInternalMcpProvider>();
        internalProvider.GetServers().Returns([new McpServerConfig { Name = "cockpit-session", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" }]);

        var viewModel = new McpServersViewModel(store, [internalProvider]);
        await viewModel.LoadAsync();
        viewModel.Servers.Single().Name = "cockpit-session";

        await viewModel.SaveCommand.ExecuteAsync(null);

        // The cockpit's own servers are filtered out of this list but share the namespace, and the catalog's merge
        // lets them win — so a name taken from one meant the operator's server was configured, saved, ticked, and
        // silently not there.
        Assert.Contains("cockpit-session", viewModel.StatusMessage);
        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
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
