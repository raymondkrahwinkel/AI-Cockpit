using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What the sign-in surface got wrong about a server's identity (AC-355/AC-499), each case found by review rather
/// than by a green suite. They share one root: a token is filed under a name the operator may retype, and the row
/// kept trying to work out which name that was. Five review rounds moved that guess rather than removing it — a
/// dialog that would not open, a withdrawal that withdrew nothing, a token filed under a placeholder — until the
/// guess was taken away by requiring a manual save before either action was offered (AC-355). AC-499 removed that
/// manual step in turn: a sign-in now saves itself and reads back the real stored name, so the guess still cannot
/// come back, but the operator no longer has to save by hand first. Sign-out kept the AC-355 shape — it still needs
/// a real stored name, just not one that matches what is currently typed.
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

    /// <summary>An <see cref="IMcpServerStore"/> substitute that actually remembers what it was told to save — the
    /// tests below need Sign in's save-then-reread-the-store step (AC-499) to see its own write, not a static stub.</summary>
    private static IMcpServerStore _RecordingStore(IEnumerable<McpServerConfig>? seed = null)
    {
        var backing = seed?.ToList() ?? [];
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<McpServerConfig>)backing.ToList());
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                backing.Clear();
                backing.AddRange(callInfo.ArgAt<IReadOnlyList<McpServerConfig>>(0));
                return Task.CompletedTask;
            });
        return store;
    }

    [Fact]
    public async Task SignOut_AfterAnUnsavedRename_StillActsUnderTheNameTheStoreKnows()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        var editable = new EditableMcpServerViewModel(_OAuthServer(), coordinator);
        await editable.RefreshAuthStateAsync();

        editable.Name = "depot-renamed";

        // AC-499: sign-out is a narrower act than sign-in — it withdraws whatever the store already has filed, which
        // does not change just because the row is mid-rename and not yet saved. Gating it on the typed name matching
        // (the pre-AC-499 behavior) would refuse a withdrawal the operator is entitled to make.
        Assert.True(editable.SignOutCommand.CanExecute(null));

        await editable.SignOutCommand.ExecuteAsync(null);

        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARowBuiltWithNoSaveRoute_ReachesTheCoordinatorForNeither_EvenIfDrivenPastItsGate()
    {
        // This row is constructed directly, the way a test does but the real dialog never would — so it has no
        // save-all delegate to sign in through. The Sign in button itself now looks enabled (the row is valid,
        // AC-499), which is exactly why the body still has to hold the line: a row with nowhere to save cannot
        // reach the coordinator under any name, guessed or otherwise.
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "new server", Command = "npx" }, coordinator, isPersisted: false);
        editable.Name = "depot";
        editable.Transport = McpTransport.Http;
        editable.Url = "https://depot.example/mcp";
        editable.Auth = McpServerAuth.OAuth;

        Assert.True(editable.SignInCommand.CanExecute(null));
        Assert.False(editable.SignOutCommand.CanExecute(null));

        await editable.SignInCommand.ExecuteAsync(null);
        await editable.SignOutCommand.ExecuteAsync(null);

        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().SignOutAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAuthState_ForARowStoredWithATrailingSpace_LooksItUpByTheTrimmedName()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);

        // A hand-edited config, or one an older build wrote before saving started trimming.
        var editable = new EditableMcpServerViewModel(_OAuthServer("depot "), coordinator);

        Assert.True(editable.SignInCommand.CanExecute(null));
        Assert.Empty(editable.SignInUnavailableReason);
        await editable.RefreshAuthStateAsync();
        await coordinator.Received().GetStateAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_AfterEditingTheUrl_AuthorizesAgainstWhatIsTypedUnderTheStoredName()
    {
        var store = _RecordingStore([_OAuthServer()]);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();
        var editable = viewModel.Servers.Single();

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
    public async Task SignIn_OnANeverSavedServer_SavesThenSignsInInOneClick()
    {
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        viewModel.AddServerCommand.Execute(null);
        var row = viewModel.Servers.Single();
        row.Name = "depot";
        row.Transport = McpTransport.Http;
        row.Url = "https://depot.example/mcp";
        row.Auth = McpServerAuth.OAuth;

        // AC-499: no manual Save first — one click on Sign in saves the row and then authorizes it.
        await row.SignInCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list => list.Any(server => server.Name == "depot")),
            Arg.Any<CancellationToken>());
        await coordinator.Received(1).AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"), true, Arg.Any<CancellationToken>());
        Assert.Equal(McpAuthState.Authorized, row.AuthState);
    }

    [Fact]
    public async Task SignIn_AfterARename_SignsInUnderTheNewStoredName_NotTheOld()
    {
        var store = _RecordingStore([_OAuthServer()]);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();
        var row = viewModel.Servers.Single();

        // A rename no longer withdraws the offer (AC-499) — Sign in stays available and, when clicked, saves the
        // rename first and authorizes under the name that lands in the store, not the one it replaced.
        row.Name = "depot-vault";
        Assert.True(row.SignInCommand.CanExecute(null));

        await row.SignInCommand.ExecuteAsync(null);

        await coordinator.Received(1).AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot-vault"), true, Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "depot"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_WithAnotherInvalidRowInTheList_BlocksWithTheValidationReason_AndDoesNotSignIn()
    {
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var viewModel = new McpServersViewModel(store, [], coordinator);

        viewModel.AddServerCommand.Execute(null);
        var oauthRow = viewModel.Servers.Single();
        oauthRow.Name = "depot";
        oauthRow.Transport = McpTransport.Http;
        oauthRow.Url = "https://depot.example/mcp";
        oauthRow.Auth = McpServerAuth.OAuth;

        viewModel.AddServerCommand.Execute(null);
        var invalidRow = viewModel.Servers.Last();
        invalidRow.Name = string.Empty;
        invalidRow.Command = string.Empty;

        // AC-499: the store is one list, so a sign-in's own save carries every row along — an unrelated invalid row
        // blocks it the same way it would block the Save button, and the row asking to sign in has to say why.
        await oauthRow.SignInCommand.ExecuteAsync(null);

        Assert.Contains("name", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(oauthRow.AuthMessage);
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_WhenTheStoreThrowsOnSave_DoesNotSignIn_AndSaysSoOnTheRow()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("disk full"));
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        viewModel.AddServerCommand.Execute(null);
        var row = viewModel.Servers.Single();
        row.Name = "depot";
        row.Transport = McpTransport.Http;
        row.Url = "https://depot.example/mcp";
        row.Auth = McpServerAuth.OAuth;

        await row.SignInCommand.ExecuteAsync(null);

        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.NotEmpty(row.AuthMessage);
        Assert.DoesNotContain("disk full", row.AuthMessage);
    }

    [Fact]
    public async Task SignIn_ClickedTwiceQuickly_SavesAndSignsInOnlyOnce()
    {
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var acquireGate = new TaskCompletionSource<McpOAuthAccess>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => acquireGate.Task);
        var viewModel = new McpServersViewModel(store, [], coordinator);

        viewModel.AddServerCommand.Execute(null);
        var row = viewModel.Servers.Single();
        row.Name = "depot";
        row.Transport = McpTransport.Http;
        row.Url = "https://depot.example/mcp";
        row.Auth = McpServerAuth.OAuth;

        // A real button checks CanExecute before invoking Execute (ICommand.Execute itself does not) — that is what
        // makes a double-click safe: IsAuthBusy flips true synchronously before the first save even starts, so the
        // second click's own CanExecute check should already see it and skip.
        if (row.SignInCommand.CanExecute(null))
        {
            row.SignInCommand.Execute(null);
        }

        if (row.SignInCommand.CanExecute(null))
        {
            row.SignInCommand.Execute(null);
        }

        acquireGate.SetResult(McpOAuthAccess.Authorized("token"));
        await row.SignInCommand.ExecutionTask!;

        await store.Received(1).SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
        await coordinator.Received(1).AcquireAsync(Arg.Any<McpServerConfig>(), true, Arg.Any<CancellationToken>());
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
    public void ARowMissingAUrl_OffersNoSignIn_AndSaysWhy()
    {
        // AC-499 replaced the "save first" gate with a plain validity one: a row still needs a name and, for http,
        // a URL — the fields SignInUnavailableReason names — before Sign in does anything.
        var editable = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "depot", Transport = McpTransport.Http, Auth = McpServerAuth.OAuth },
            Substitute.For<IMcpOAuthCoordinator>(),
            isPersisted: false);

        Assert.False(editable.SignInCommand.CanExecute(null));
        Assert.False(editable.SignOutCommand.CanExecute(null));
        Assert.NotEmpty(editable.SignInUnavailableReason);

        editable.Url = "https://depot.example/mcp";

        Assert.True(editable.SignInCommand.CanExecute(null));
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
        var editable = _RowWithWiredSave(coordinator);

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
        var editable = _RowWithWiredSave(coordinator);

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

        return _RowWithWiredSave(coordinator);
    }

    /// <summary>A single already-saved OAuth row with a real save-all delegate wired (AC-499) — what a sign-in
    /// needs to actually reach the coordinator, since the row no longer calls it directly on its own.</summary>
    private static EditableMcpServerViewModel _RowWithWiredSave(IMcpOAuthCoordinator coordinator)
    {
        var store = _RecordingStore([_OAuthServer()]);
        var viewModel = new McpServersViewModel(store, [], coordinator);
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return viewModel.Servers.Single();
    }

    // AC-499 review-fix regression tests below (host half). Each pins one of the findings from the adversarial
    // review of AC-499's "save first, then sign in" dance — the common root, spelled out in the ticket, is that a
    // successful dialog-wide save used to leave the dialog open without bringing every row back in line with the
    // store, not just the row that clicked.

    [Fact]
    public async Task DialogWideSave_ResyncsEveryRowsStoredName_NotJustTheOneThatSignedIn()
    {
        // Finding 1 (BLOCKER): two rows swap names in one edit — still unique, so nothing blocks the save. Alpha
        // becomes Beta and signs in there; Beta becomes Alpha and must not be left thinking it still owns "Beta",
        // or its own Sign out would withdraw the token Alpha just acquired.
        var store = _RecordingStore([_OAuthServer("Alpha"), _OAuthServer("Beta")]);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Is<McpServerConfig>(server => server.Name == "Alpha"), Arg.Any<CancellationToken>())
            .Returns(McpAuthState.Authorized);
        coordinator.GetStateAsync(Arg.Is<McpServerConfig>(server => server.Name == "Beta"), Arg.Any<CancellationToken>())
            .Returns(McpAuthState.Authorized);
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));

        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();
        var rowA = viewModel.Servers[0];
        var rowB = viewModel.Servers[1];

        rowA.Name = "Beta";
        rowB.Name = "Alpha";

        await rowA.SignInCommand.ExecuteAsync(null);

        Assert.True(rowB.SignOutCommand.CanExecute(null));
        await rowB.SignOutCommand.ExecuteAsync(null);

        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "Alpha"), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.Name == "Beta"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignIn_WhenTheStoreThrowsOnLoadAfterASuccessfulSave_DoesNotClaimNothingWasSaved()
    {
        // Finding 2A: the write already happened — LoadAsync just could not confirm it. The old message ("Sign-in
        // failed. Try again.") reads as "nothing was saved", which is a different, false claim.
        var store = Substitute.For<IMcpServerStore>();
        var loadCall = 0;
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => loadCall++ == 0
            ? Task.FromResult<IReadOnlyList<McpServerConfig>>(new List<McpServerConfig>())
            : throw new IOException("disk hiccup"));
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        viewModel.AddServerCommand.Execute(null);
        var row = viewModel.Servers.Single();
        row.Name = "depot";
        row.Transport = McpTransport.Http;
        row.Url = "https://depot.example/mcp";
        row.Auth = McpServerAuth.OAuth;

        await row.SignInCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("Sign-in failed", row.AuthMessage, StringComparison.Ordinal);
        Assert.Contains("Saved", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_WhenTheStoreThrowsOnLoadAfterASuccessfulSave_DoesNotThrowAndDoesNotClose()
    {
        // Finding 2B: LoadAsync used to sit outside every try/catch in _SaveAllForSignInAsync, so this exact
        // scenario propagated all the way out of the Save button's own [RelayCommand] method.
        var store = Substitute.For<IMcpServerStore>();
        var loadCall = 0;
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => loadCall++ == 0
            ? Task.FromResult<IReadOnlyList<McpServerConfig>>(new List<McpServerConfig>())
            : throw new IOException("disk hiccup"));
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var viewModel = new McpServersViewModel(store, []);
        await viewModel.LoadAsync();

        viewModel.AddServerCommand.Execute(null);
        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        // Awaiting the command's own task is what would surface an exception that escaped the [RelayCommand]
        // method uncaught — a real button click does not await it, which is exactly how this used to go unnoticed.
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains("Saved", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignIn_ClearsAStaleHiddenServersNotice_TheSaveJustActedOnIt()
    {
        // Finding 3a: the notice names servers this Sign in's own save is about to drop from the store — true when
        // it was written, false the moment the save it warns about has actually run.
        var store = _RecordingStore([
            _OAuthServer("mine"),
            new McpServerConfig { Name = "plugin-server", Transport = McpTransport.Http, Url = "https://ours.example/mcp" },
        ]);
        var provider = Substitute.For<ICockpitInternalMcpProvider>();
        provider.GetServers().Returns([new McpServerConfig { Name = "plugin-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" }]);
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));

        var viewModel = new McpServersViewModel(store, [provider], coordinator);
        await viewModel.LoadAsync();
        Assert.Contains("plugin-server", viewModel.StatusMessage);

        await viewModel.Servers.Single().SignInCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.StatusMessage);
    }

    [Fact]
    public async Task SignIn_ClearsAnEarlierValidationFailure_OnceItSucceeds()
    {
        // Finding 3b: unreachable before AC-499 (a successful save always closed the dialog); now the operator can
        // fix the problem and sign in without the earlier refusal still sitting under the success.
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        viewModel.AddServerCommand.Execute(null);
        var oauthRow = viewModel.Servers.Single();
        oauthRow.Name = "depot";
        oauthRow.Transport = McpTransport.Http;
        oauthRow.Url = "https://depot.example/mcp";
        oauthRow.Auth = McpServerAuth.OAuth;

        viewModel.AddServerCommand.Execute(null);
        var invalidRow = viewModel.Servers.Last();
        invalidRow.Name = string.Empty;
        invalidRow.Command = string.Empty;

        await oauthRow.SignInCommand.ExecuteAsync(null);
        Assert.NotEmpty(viewModel.StatusMessage);

        invalidRow.Name = "second";
        invalidRow.Command = "npx";
        await oauthRow.SignInCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.StatusMessage);
    }

    [Fact]
    public async Task SignIn_OnOneRow_DisablesAnotherRowsSignIn_AndSaveAndCancel()
    {
        // Finding 6: busy used to be per row while the save it triggers is dialog-wide — a second row's own Sign in
        // could fire a second save-then-authorize while the first was still mid-flight.
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var acquireGate = new TaskCompletionSource<McpOAuthAccess>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => acquireGate.Task);
        var viewModel = new McpServersViewModel(store, [], coordinator);

        viewModel.AddServerCommand.Execute(null);
        var rowA = viewModel.Servers.Single();
        rowA.Name = "depot-a";
        rowA.Transport = McpTransport.Http;
        rowA.Url = "https://a.example/mcp";
        rowA.Auth = McpServerAuth.OAuth;

        viewModel.AddServerCommand.Execute(null);
        var rowB = viewModel.Servers.Last();
        rowB.Name = "depot-b";
        rowB.Transport = McpTransport.Http;
        rowB.Url = "https://b.example/mcp";
        rowB.Auth = McpServerAuth.OAuth;

        var signInTask = rowA.SignInCommand.ExecuteAsync(null);

        Assert.False(rowB.SignInCommand.CanExecute(null));
        Assert.False(viewModel.SaveCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));

        acquireGate.SetResult(McpOAuthAccess.Authorized("token"));
        await signInTask;

        Assert.True(rowB.SignInCommand.CanExecute(null));
        Assert.True(viewModel.SaveCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingTheDialog_WhileASignInIsInFlight_CancelsTheAcquireCall()
    {
        // Finding 6: AcquireAsync used to get no CancellationToken at all, so a dialog closed mid-sign-in (the OS
        // close button, here simulated through OnWindowClosed) left the call running with nowhere to land.
        var store = _RecordingStore();
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        var acquireStarted = new TaskCompletionSource();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                acquireStarted.SetResult();
                await Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(2));
                return McpOAuthAccess.Authorized("token"); // unreachable
            });
        var viewModel = new McpServersViewModel(store, [], coordinator);

        viewModel.AddServerCommand.Execute(null);
        var row = viewModel.Servers.Single();
        row.Name = "depot";
        row.Transport = McpTransport.Http;
        row.Url = "https://depot.example/mcp";
        row.Auth = McpServerAuth.OAuth;

        var signInTask = row.SignInCommand.ExecuteAsync(null);
        await acquireStarted.Task;

        viewModel.OnWindowClosed();

        var completed = await Task.WhenAny(signInTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(signInTask, completed);
    }
}
