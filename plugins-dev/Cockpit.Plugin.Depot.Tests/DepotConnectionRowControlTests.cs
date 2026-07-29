using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Ui;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotConnectionRowControl"/>'s sign-in availability guard (AC-243/AC-355): a token is filed under a
/// server's registered name, so asking to sign in — or even reading standing for — a row whose current name has
/// drifted from what is actually saved would file it under, or ask about, a name the store may not have. Both
/// <see cref="DepotConnectionRowControl.RefreshAuthStateAsync"/> and the Sign-in action share this one guard;
/// exercising it through the public read path is enough to prove it, without also driving the async Click handler.
/// </summary>
[Collection("avalonia")]
public class DepotConnectionRowControlTests
{
    [Fact]
    public async Task RefreshAuthStateAsync_NewUnsavedRow_NeverAsksTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        var row = new DepotConnectionRowControl(host, existing: null);

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAuthStateAsync_SavedRowWithUnchangedName_AsksTheHostForThatExactStoredName()
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetMcpServerAuthStateAsync("Depot: Work", Arg.Any<CancellationToken>()).Returns(PluginMcpAuthState.Authorized);
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");
        var row = new DepotConnectionRowControl(host, existing);

        await row.RefreshAuthStateAsync();

        _ = host.Received(1).GetMcpServerAuthStateAsync("Depot: Work", Arg.Any<CancellationToken>());
    }

    // The guard itself: a saved row whose name box was edited (a rename not yet saved) must not ask about — or,
    // by the same code path, sign in under — the old stored name, because once saved the token would be filed
    // under a name the row no longer shows.
    [Fact]
    public async Task RefreshAuthStateAsync_RowRenamedButNotYetSaved_NeverAsksTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");
        var row = new DepotConnectionRowControl(host, existing);
        _Show(row);
        row.GetVisualDescendants().OfType<TextBox>().First().Text = "Work (renamed)";

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The guard's URL half: signing in with an edited-but-unsaved URL would authorize against an issuer the
    // connection is not (yet) saved as pointing at, under a name that — once saved — points somewhere else.
    [Fact]
    public async Task RefreshAuthStateAsync_RowsUrlEditedButNotYetSaved_NeverAsksTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://old.example.com");
        var row = new DepotConnectionRowControl(host, existing);
        _Show(row);
        row.GetVisualDescendants().OfType<TextBox>().ElementAt(1).Text = "https://new.example.com";

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsBlank_NewRowWithNothingEntered_IsTrue()
    {
        var row = new DepotConnectionRowControl(Substitute.For<ICockpitHost>(), existing: null);

        Assert.True(row.IsBlank);
    }

    [Fact]
    public void IsBlank_ExistingRow_IsNeverTrue_EvenBeforeAnyEdit()
    {
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");
        var row = new DepotConnectionRowControl(Substitute.For<ICockpitHost>(), existing);

        Assert.False(row.IsBlank);
    }

    [Fact]
    public void ToRegistration_TrimsTheUrlsTrailingSlash()
    {
        var row = new DepotConnectionRowControl(Substitute.For<ICockpitHost>(), existing: null);
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com/";

        var registration = row.ToRegistration();

        Assert.Equal("https://depot.example.com", registration.Url);
    }

    private static void _Show(Avalonia.Controls.Control control)
    {
        var window = new Window { Content = control };
        window.Show();
        window.UpdateLayout();
    }
}
