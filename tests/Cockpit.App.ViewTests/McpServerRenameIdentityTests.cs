using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// Renaming a server in the MCP-servers dialog keeps its sign-in, and cannot hand it to anybody else (AC-403).
/// <para>
/// The defect these pin down: a token was filed under the server's name, and the name is a field the operator edits.
/// Saving a rename wrote the new name into the registry and left the token behind under the old one — unreachable,
/// still holding a refresh token, and the row reporting "sign-in needed" over a credential that was sitting right
/// there. Worse in the swap case below: two rows exchanging names both pass the uniqueness check, so each would end
/// up pointing at the other's token, and <see cref="McpOAuthToken.IsForResource"/> only bounds a token to
/// scheme/host/port — two servers on one host with different paths pass that check, and one server's bearer goes to
/// an endpoint it was never issued for.
/// </para>
/// <para>
/// Asserted on <see cref="McpServerConfig.IdentityKey"/> throughout, because that is the key the coordinator hands
/// the token store: asserting on the name would pass just as well against the behaviour these exist to prevent.
/// </para>
/// </summary>
public class McpServerRenameIdentityTests
{
    private static McpServerConfig _OAuthServer(string id, string name, string url = "https://depot.example/mcp") => new()
    {
        Id = id,
        Name = name,
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
    };

    /// <summary>An <see cref="IMcpServerStore"/> substitute that remembers what it was told to save — the save-then-reread step a sign-in performs has to see its own write.</summary>
    private static IMcpServerStore _RecordingStore(IEnumerable<McpServerConfig> seed)
    {
        var backing = seed.ToList();
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

    private static IMcpOAuthCoordinator _AlwaysAuthorized()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.Authorized);
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(McpOAuthAccess.Authorized("token"));
        return coordinator;
    }

    [Fact]
    public async Task Rename_ThenSave_KeepsTheServerFiledUnderTheSameId()
    {
        // Acceptance criterion 2. The row's name changes and is written; the key its credential lives under does
        // not move with it, so the session that comes next still finds the sign-in.
        var store = _RecordingStore([_OAuthServer("server-id", "depot")]);
        var viewModel = new McpServersViewModel(store, [], _AlwaysAuthorized());
        await viewModel.LoadAsync();

        viewModel.Servers.Single().Name = "depot renamed";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var persisted = Assert.Single(await store.LoadAsync());
        Assert.Equal("depot renamed", persisted.Name);
        Assert.Equal("server-id", persisted.IdentityKey);
    }

    [Fact]
    public async Task Rename_ThenSave_LeavesNoSecondEntryForTheOldName()
    {
        // Acceptance criterion 4, at the registry: one row in, one row out. A save that added the new name beside
        // the old one is the shape that leaves an orphan for the token store to keep answering about.
        var store = _RecordingStore([_OAuthServer("server-id", "depot")]);
        var viewModel = new McpServersViewModel(store, [], _AlwaysAuthorized());
        await viewModel.LoadAsync();

        viewModel.Servers.Single().Name = "depot renamed";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Single(await store.LoadAsync());
    }

    [Fact]
    public async Task RenamedRow_AsksTheCoordinatorAboutItsOwnId_NotTheNameItNowCarries()
    {
        var store = _RecordingStore([_OAuthServer("server-id", "depot")]);
        var coordinator = _AlwaysAuthorized();
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        var row = viewModel.Servers.Single();
        row.Name = "depot renamed";
        await viewModel.SaveCommand.ExecuteAsync(null);
        await row.RefreshAuthStateAsync();

        await coordinator.Received().GetStateAsync(
            Arg.Is<McpServerConfig>(server => server.IdentityKey == "server-id" && server.Name == "depot renamed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TwoServersOnOneHostThatSwapNames_KeepTheirOwnIds()
    {
        // Acceptance criterion 3, the host half — and the reason direction 2 was chosen over moving the token along
        // with the rename. Same host, different paths, names exchanged in one edit: both stay unique, so the save
        // goes through, and the origin check cannot tell the two apart. Only the id can.
        var store = _RecordingStore([
            _OAuthServer("id-alpha", "alpha", "https://depot.example/alpha"),
            _OAuthServer("id-beta", "beta", "https://depot.example/beta"),
        ]);
        var viewModel = new McpServersViewModel(store, [], _AlwaysAuthorized());
        await viewModel.LoadAsync();

        viewModel.Servers[0].Name = "beta";
        viewModel.Servers[1].Name = "alpha";
        await viewModel.SaveCommand.ExecuteAsync(null);

        var persisted = await store.LoadAsync();
        Assert.Equal("id-alpha", Assert.Single(persisted, server => server.Name == "beta").IdentityKey);
        Assert.Equal("id-beta", Assert.Single(persisted, server => server.Name == "alpha").IdentityKey);
    }

    [Fact]
    public async Task SignOut_AfterASwap_WithdrawsOnlyTheRowsOwnCredential()
    {
        // The same swap, followed by the act that would do the damage. The row that is now called "alpha" must
        // withdraw what it signed in for, not what the other row just acquired under that name.
        var store = _RecordingStore([
            _OAuthServer("id-alpha", "alpha", "https://depot.example/alpha"),
            _OAuthServer("id-beta", "beta", "https://depot.example/beta"),
        ]);
        var coordinator = _AlwaysAuthorized();
        var viewModel = new McpServersViewModel(store, [], coordinator);
        await viewModel.LoadAsync();

        var rowAlpha = viewModel.Servers[0];
        var rowBeta = viewModel.Servers[1];
        rowAlpha.Name = "beta";
        rowBeta.Name = "alpha";

        await rowAlpha.SignInCommand.ExecuteAsync(null);
        await rowBeta.SignOutCommand.ExecuteAsync(null);

        await coordinator.Received().AcquireAsync(
            Arg.Is<McpServerConfig>(server => server.IdentityKey == "id-alpha"), true, Arg.Any<CancellationToken>());
        await coordinator.Received().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.IdentityKey == "id-beta"), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().SignOutAsync(
            Arg.Is<McpServerConfig>(server => server.IdentityKey == "id-alpha"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARowFromAConfigWithoutIds_KeepsTheIdItsOldNameDerivedTo_AcrossARename()
    {
        // The upgrade path in the dialog. A row read from a pre-AC-403 config has no id of its own and answers to
        // the one its name derives to — the same key its already-stored token is found by. Renaming it must pin
        // that derived id down rather than re-derive from the new name, or the very first rename after upgrading
        // would strand the credential the derivation was there to rescue.
        var store = _RecordingStore([new McpServerConfig
        {
            Name = "depot",
            Transport = McpTransport.Http,
            Url = "https://depot.example/mcp",
            Auth = McpServerAuth.OAuth,
        }]);
        var viewModel = new McpServersViewModel(store, [], _AlwaysAuthorized());
        await viewModel.LoadAsync();

        viewModel.Servers.Single().Name = "depot renamed";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(McpServerIdentity.LegacyIdFor("depot"), Assert.Single(await store.LoadAsync()).IdentityKey);
    }

    [Fact]
    public void AddServer_MintsAnIdOfItsOwn_NotOneDerivedFromThePlaceholderName()
    {
        // A new row is going to be renamed — the placeholder name exists to be replaced. Deriving its id from that
        // placeholder would tie the identity to the one name we know for certain is about to change.
        var viewModel = new McpServersViewModel(_RecordingStore([]), [], _AlwaysAuthorized());
        viewModel.AddServerCommand.Execute(null);

        var added = viewModel.Servers.Single();
        Assert.NotEqual(McpServerIdentity.LegacyIdFor(added.Name), added.Id);
        Assert.NotEmpty(added.Id);
    }

    [Fact]
    public void AddServer_TwiceInOneDialog_MintsTwoDifferentIds()
    {
        var viewModel = new McpServersViewModel(_RecordingStore([]), [], _AlwaysAuthorized());
        viewModel.AddServerCommand.Execute(null);
        viewModel.AddServerCommand.Execute(null);

        Assert.Equal(2, viewModel.Servers.Select(server => server.Id).Distinct(StringComparer.Ordinal).Count());
    }
}
