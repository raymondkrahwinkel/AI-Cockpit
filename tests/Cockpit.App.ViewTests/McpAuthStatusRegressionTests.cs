using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
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
    public async Task SignOut_AfterAnUnsavedRename_StillActsOnWhatTheStoreHasFiled()
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

        // Asserted on the id rather than the name since AC-403: the credential is filed under the id, and the row's
        // id is untouched by an unsaved rename — which is what makes "still acts on what the store has" true here
        // without the row having to work out which name that was.
        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.IdentityKey == McpServerIdentity.LegacyIdFor("depot")),
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
