using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What the sign-in surface got wrong about a server's identity (AC-355), each case found by review rather than by a
/// green suite. They share one root: a token is filed under a name the operator may retype, and the row kept trying
/// to work out which name that was. Five review rounds moved that guess rather than removing it — a dialog that would
/// not open, a withdrawal that withdrew nothing, a token filed under a placeholder — until the guess was taken away
/// instead, by only offering the actions while the row and the store agree on the name.
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
    public async Task SignOut_EvenIfDrivenPastItsGate_ActsUnderTheNameTheStoreKnows()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();

        editable.Name = "depot-renamed";
        Assert.False(editable.SignOutCommand.CanExecute(null));

        // The gate is what the operator meets, but AsyncRelayCommand.ExecuteAsync does not consult CanExecute — so
        // the body has to hold the line on its own. Withdrawing under the newly typed name removed nothing while
        // reporting the access as gone; the bearer stayed in cockpit.json behind a reassuring badge.
        await editable.SignOutCommand.ExecuteAsync(null);

        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARowThatWasNeverSaved_ReachesTheCoordinatorForNeither_EvenIfDrivenPastItsGate()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "new server", Command = "npx" }, coordinator, isPersisted: false);
        editable.Name = "depot";
        editable.Transport = McpTransport.Http;
        editable.Url = "https://depot.example/mcp";
        editable.Auth = McpServerAuth.OAuth;

        await editable.SignInCommand.ExecuteAsync(null);
        await editable.SignOutCommand.ExecuteAsync(null);

        // The gate is what the operator meets; ExecuteAsync walks past it, so the bodies hold the line themselves.
        // Without that, a row with no stored name reaches the coordinator under whatever is typed — which is the
        // guess this ticket spent five review rounds removing.
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().SignOutAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARowStoredWithATrailingSpace_StillOffersItsSignIn()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);

        // A hand-edited config, or one an older build wrote before saving started trimming. Comparing the stored name
        // untrimmed against the typed one meant the gate could never open, and the reason shown blamed a rename that
        // never happened — the last untrimmed name comparison in a family the rest of this ticket already trimmed.
        var editable = new EditableMcpServerViewModel(_OAuthServer("depot "), coordinator);

        Assert.True(editable.IsSignInAvailable);
        Assert.Empty(editable.SignInUnavailableReason);
        await editable.RefreshAuthStateAsync();
        await coordinator.Received().GetStateAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_AfterEditingTheUrl_AuthorizesAgainstWhatIsTypedUnderTheStoredName()
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
    public void ARowThatWasNeverSaved_OffersNoSignIn()
    {
        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "new server", Command = "npx" },
            Substitute.For<IMcpOAuthCoordinator>(),
            isPersisted: false);
        editable.Name = "depot";
        editable.Transport = McpTransport.Http;
        editable.Url = "https://depot.example/mcp";
        editable.Auth = McpServerAuth.OAuth;

        // A sign-in is filed under a server's name, and this row has no name in the store yet — whatever it is called
        // right now is what the operator is still typing. Four rounds of review each found the same defect one case
        // further along because the row guessed which name its token was under; not acting until there is a name to
        // file under removes the guess instead of refining it.
        Assert.False(editable.IsSignInAvailable);
        Assert.False(editable.SignInCommand.CanExecute(null));
        Assert.False(editable.SignOutCommand.CanExecute(null));
        Assert.NotEmpty(editable.SignInUnavailableReason);
    }

    [Fact]
    public async Task RenamingAServer_WithdrawsTheOfferUntilTheNameIsPutBack()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();
        Assert.True(editable.SignInCommand.CanExecute(null));

        editable.Name = "vault";

        // While the typed name and the stored one disagree, neither action has a name it could honestly act on. Said
        // rather than left as a dead button — and reversible, because putting the name back is all it takes.
        Assert.False(editable.IsSignInAvailable);
        Assert.False(editable.SignOutCommand.CanExecute(null));
        Assert.NotEmpty(editable.SignInUnavailableReason);

        editable.Name = "depot";

        Assert.True(editable.IsSignInAvailable);
        Assert.True(editable.SignOutCommand.CanExecute(null));
        Assert.Empty(editable.SignInUnavailableReason);
    }

    [Fact]
    public async Task Load_SaysWhichSavedServersAreHiddenBecauseTheCockpitRunsOneByThatName()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>
        {
            new() { Name = "mine", Transport = McpTransport.Http, Url = "https://mine.example/mcp" },
            new() { Name = "plugin-server", Transport = McpTransport.Http, Url = "https://ours.example/mcp" },
        });

        var provider = Substitute.For<ICockpitInternalMcpProvider>();
        provider.GetServers().Returns([new McpServerConfig { Name = "plugin-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" }]);

        var viewModel = new McpServersViewModel(store, [provider]);
        await viewModel.LoadAsync();

        // The row is filtered out of the dialog and the next save writes only what the dialog holds — so an entry the
        // operator configured, under a name a plugin has since taken, is deleted. Tidying up a leftover an older
        // build wrote is the intent; doing either without a word is not.
        Assert.Single(viewModel.Servers);
        Assert.Contains("plugin-server", viewModel.StatusMessage);
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

    [Fact]
    public async Task SignInThatNeverReachedABrowser_DoesNotSendTheOperatorToLookAtOne()
    {
        var editable = _RowStoppingAt(McpSignInStage.NoBrowserLaunched);

        await editable.SignInCommand.ExecuteAsync(null);

        // Found live (AC-457): the server's discovery document was refused, so the authorization URL was never known
        // and the code that hands one to a browser was never reached — while the cockpit reported it as a browser
        // window the operator had failed to finish with. There was nothing to check. Saying a browser was never
        // reached is the point and stays; naming a window to go and look at is what may not.
        Assert.DoesNotContain("browser window", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(editable.AuthMessage);
    }

    [Fact]
    public void TheThreeStages_EachGetTheirOwnWording()
    {
        var messages = new[]
        {
            McpSignInStage.NoBrowserLaunched,
            McpSignInStage.BrowserRequested,
            McpSignInStage.AuthorizationReturned,
        }.Select(_MessageFor).ToArray();

        // The stages exist to be told apart. Collapsing them back into one sentence that is safe everywhere is the
        // failure mode the ticket names: it would be true, and it would say nothing the operator can act on.
        Assert.Equal(messages.Length, messages.Distinct(StringComparer.Ordinal).Count());
    }

    private static string _MessageFor(McpSignInStage reached)
    {
        var editable = _RowStoppingAt(reached);
        editable.SignInCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return editable.AuthMessage;
    }

    [Fact]
    public async Task SignInTheBrowserNeverAnswered_SaysOnlyThatItWasHandedOver()
    {
        var editable = _RowStoppingAt(McpSignInStage.BrowserRequested);

        await editable.SignInCommand.ExecuteAsync(null);

        // The case the old fixed line was written for is still worth saying — the point was never to stop mentioning
        // the browser, but to stop mentioning it on runs that never got there. What it may not do is assert a window:
        // handing the URL to the desktop is the last thing the cockpit can observe.
        Assert.Contains("browser", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browser window", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInThatCameBackWithoutACredential_DoesNotBlameTheServer()
    {
        var editable = _RowStoppingAt(McpSignInStage.AuthorizationReturned);

        await editable.SignInCommand.ExecuteAsync(null);

        // A sign-in that succeeds and issues a credential with less life left than the margin lands on this stage
        // too (McpOAuthCoordinator's ExpiryMargin). On that run nothing refused anything, so a sentence saying the
        // server refused would be the ticket's own defect wearing the ticket's own fix.
        Assert.DoesNotContain("refused", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("credential", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignInThatThrew_DoesNotBlameTheUrlOrTheOAuthSettings()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<McpOAuthAccess>(_ => throw new IOException("the config file could not be written"));
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);

        await editable.SignInCommand.ExecuteAsync(null);

        // The sibling of the switch above, and the same class of defect: what reaches this catch is whatever escaped
        // the coordinator — a config write that failed, say — which is neither the address nor the OAuth settings.
        // Repairing the switch and leaving this naming a cause it cannot know would be one instance, not the class.
        Assert.DoesNotContain("URL", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OAuth settings", editable.AuthMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(editable.AuthMessage);
    }

    private static EditableMcpServerViewModel _RowStoppingAt(McpSignInStage reached)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.AuthorizationRequired with { SignInStage = reached });

        return new EditableMcpServerViewModel(_OAuthServer(), coordinator);
    }
}
