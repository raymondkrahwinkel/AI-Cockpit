using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;
using Cockpit.Plugin.Depot.Ui;
using NSubstitute;

namespace Cockpit.Plugin.Depot.Tests;

/// <summary>
/// <see cref="DepotConnectionRowControl"/>'s Sign-in action (AC-243/AC-355, reworked AC-499): a token is filed
/// under a server's registered name, so the row must never sign in under a name it merely typed — it saves first
/// (through the delegate it is constructed with, the same route <see cref="Ui.DepotSettingsControl.Save"/> exposes
/// to its own Save button) and then re-reads what actually landed in storage before calling the host. The
/// read-availability guard <see cref="DepotConnectionRowControl.RefreshAuthStateAsync"/> uses is exercised
/// separately below; it is unrelated to whether Sign-in itself is offered since AC-499.
/// </summary>
[Collection("avalonia")]
public class DepotConnectionRowControlTests
{
    [Fact]
    public async Task RefreshAuthStateAsync_NewUnsavedRow_NeverAsksTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        var row = _NewRow(host, existing: null);

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAuthStateAsync_SavedRowWithUnchangedName_AsksTheHostForThatExactStoredName()
    {
        var host = Substitute.For<ICockpitHost>();
        host.GetMcpServerAuthStateAsync("Depot: Work", Arg.Any<CancellationToken>()).Returns(PluginMcpAuthState.Authorized);
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");
        var row = _NewRow(host, existing);

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
        var row = _NewRow(host, existing);
        _Show(row);
        row.GetVisualDescendants().OfType<TextBox>().First().Text = "Work (renamed)";

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The guard's URL half: reading auth state under an edited-but-unsaved URL would ask about an issuer the
    // connection is not (yet) saved as pointing at.
    [Fact]
    public async Task RefreshAuthStateAsync_RowsUrlEditedButNotYetSaved_NeverAsksTheHost()
    {
        var host = Substitute.For<ICockpitHost>();
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://old.example.com");
        var row = _NewRow(host, existing);
        _Show(row);
        row.GetVisualDescendants().OfType<TextBox>().ElementAt(1).Text = "https://new.example.com";

        await row.RefreshAuthStateAsync();

        _ = host.DidNotReceive().GetMcpServerAuthStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsBlank_NewRowWithNothingEntered_IsTrue()
    {
        var row = _NewRow(Substitute.For<ICockpitHost>(), existing: null);

        Assert.True(row.IsBlank);
    }

    [Fact]
    public void IsBlank_ExistingRow_IsNeverTrue_EvenBeforeAnyEdit()
    {
        var existing = new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com");
        var row = _NewRow(Substitute.For<ICockpitHost>(), existing);

        Assert.False(row.IsBlank);
    }

    [Fact]
    public void ToRegistration_TrimsTheUrlsTrailingSlash()
    {
        var row = _NewRow(Substitute.For<ICockpitHost>(), existing: null);
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com/";

        var registration = row.ToRegistration();

        Assert.Equal("https://depot.example.com", registration.Url);
    }

    // AC-499 regression: Depot's own docs tell the operator to paste the full endpoint (…/mcp), and DepotPlugin
    // appends /mcp itself when it builds the contribution — pasting the documented URL unchanged used to double
    // into "…/mcp/mcp" (404, no WWW-Authenticate, OAuth discovery never started). Stored URL must be the bare base.
    [Fact]
    public void ToRegistration_UrlWithTrailingMcp_StoresTheBaseUrlWithoutIt()
    {
        var row = _NewRow(Substitute.For<ICockpitHost>(), existing: null);
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com/mcp";

        var registration = row.ToRegistration();

        Assert.Equal("https://depot.example.com", registration.Url);
    }

    // AC-499 criterion: a row that was never saved signs in in one call — no separate save-then-reopen step.
    [Fact]
    public async Task SignInAsync_NeverSavedRow_SavesAndSignsInInOneCall()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SignInMcpServerAsync("Depot: Work", Arg.Any<CancellationToken>()).Returns(PluginMcpSignInOutcome.Authorized);
        var settings = new DepotSettings(new FakePluginStorage());
        DepotConnectionRowControl row = null!;
        var saveCount = 0;
        row = _NewRow(host, existing: null, settings, saveAll: () =>
        {
            saveCount++;
            settings.Connections = [row.ToRegistration()];
            return (true, null);
        });
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com";

        await row.SignInAsync();

        Assert.Equal(1, saveCount);
        _ = host.Received(1).SignInMcpServerAsync("Depot: Work", Arg.Any<CancellationToken>());
        _ = host.Received(1).SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SignInAsync_EmptyName_BlocksWithReason_AndNeverSavesOrSignsIn()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var saveCount = 0;
        var row = _NewRow(host, existing: null, settings, saveAll: () => { saveCount++; return (true, null); });
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = string.Empty;
        boxes[1].Text = "https://depot.example.com";

        await row.SignInAsync();

        Assert.Equal(0, saveCount);
        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("Enter a name", _AuthStatusText(row), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignInAsync_UnusableUrl_BlocksWithReason_AndNeverSavesOrSignsIn()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var saveCount = 0;
        var row = _NewRow(host, existing: null, settings, saveAll: () => { saveCount++; return (true, null); });
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "not-a-url";

        await row.SignInAsync();

        Assert.Equal(0, saveCount);
        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("usable address", _AuthStatusText(row), StringComparison.Ordinal);
    }

    // AC-499: PluginMcpSignInOutcome carries no detail of its own (its doc comment only says "a network/store
    // failure") — the row names the address it actually dialed instead of leaving the operator to guess whether the
    // URL, the network, or something else was the problem.
    [Fact]
    public async Task SignInAsync_HostReportsUnreachable_NamesTheUrlItTried()
    {
        var host = Substitute.For<ICockpitHost>();
        host.SignInMcpServerAsync("Depot: Work", Arg.Any<CancellationToken>()).Returns(PluginMcpSignInOutcome.Unreachable);
        var settings = new DepotSettings(new FakePluginStorage());
        DepotConnectionRowControl row = null!;
        row = _NewRow(host, existing: null, settings, saveAll: () =>
        {
            settings.Connections = [row.ToRegistration()];
            return (true, null);
        });
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        // Typed with the /mcp Depot's docs show — the message must name the single-/mcp endpoint actually dialed,
        // not double it and not echo the operator's raw (unstripped) input.
        boxes[1].Text = "https://depot.example.com/mcp";

        await row.SignInAsync();

        Assert.Contains("https://depot.example.com/mcp", _AuthStatusText(row), StringComparison.Ordinal);
        Assert.DoesNotContain("mcp/mcp", _AuthStatusText(row) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsBothFilled_SignInButtonIsEnabled_EvenThoughNothingIsSavedYet()
    {
        var row = _NewRow(Substitute.For<ICockpitHost>(), existing: null);
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com";
        // TextBox.TextChanged is posted to the dispatcher rather than raised inline (Avalonia's own remark: "occurs
        // asynchronously after text changes and the new text is rendered") — flush it so _OnFieldsChanged, which is
        // what recomputes the button's enabled state, has actually run before the assertion below.
        Dispatcher.UIThread.RunJobs();

        Assert.True(_SignInButton(row).IsEnabled);
    }

    // AC-499 fix: a failed save now names the connection it collided with, not just that it failed — the operator
    // staring at this row needs to know which name to change.
    [Fact]
    public async Task SignInAsync_SaveFailsOnANameCollision_NamesTheCollidingConnection_AndNeverAttemptsSignIn()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var row = _NewRow(host, existing: null, settings, saveAll: () => (false, "Work"));
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com";

        await row.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("Couldn't save", _AuthStatusText(row), StringComparison.Ordinal);
        Assert.Contains("Work", _AuthStatusText(row), StringComparison.Ordinal);
    }

    // Falls back to a generic message when saveAll fails without naming a duplicate — kept distinct from the
    // collision message above so a future non-collision save failure never gets misreported as a name clash.
    [Fact]
    public async Task SignInAsync_SaveFailsWithoutADuplicateName_ShowsAGenericMessage_AndNeverAttemptsSignIn()
    {
        var host = Substitute.For<ICockpitHost>();
        var settings = new DepotSettings(new FakePluginStorage());
        var row = _NewRow(host, existing: null, settings, saveAll: () => (false, null));
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com";

        await row.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("Couldn't save", _AuthStatusText(row), StringComparison.Ordinal);
    }

    // The double-click guard now has to cover the save leg too, not just the sign-in leg. The second call is made
    // before the first's SignInMcpServerAsync call completes (gated on signInGate), the same shape as the
    // TaskCompletionSource-based reentrancy proof BuildTraps.md documents for AC-243's host calls.
    [Fact]
    public async Task SignInAsync_CalledTwiceBeforeTheFirstCompletes_OnlySavesAndSignsInOnce()
    {
        var host = Substitute.For<ICockpitHost>();
        var signInGate = new TaskCompletionSource<PluginMcpSignInOutcome>();
        host.SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => signInGate.Task);
        var settings = new DepotSettings(new FakePluginStorage());
        DepotConnectionRowControl row = null!;
        var saveCount = 0;
        row = _NewRow(host, existing: null, settings, saveAll: () =>
        {
            saveCount++;
            settings.Connections = [row.ToRegistration()];
            return (true, null);
        });
        _Show(row);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = "Work";
        boxes[1].Text = "https://depot.example.com";

        var first = row.SignInAsync();
        var second = row.SignInAsync();
        signInGate.SetResult(PluginMcpSignInOutcome.Authorized);
        await first;
        await second;

        Assert.Equal(1, saveCount);
        _ = host.Received(1).SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static string? _AuthStatusText(DepotConnectionRowControl row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Opacity == 0.8).Text;

    private static Button _SignInButton(DepotConnectionRowControl row) =>
        row.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "Sign in"));

    private static DepotConnectionRowControl _NewRow(
        ICockpitHost host, DepotConnectionRegistration? existing, DepotSettings? settings = null,
        Func<(bool Success, string? DuplicateName)>? saveAll = null) =>
        new(host, existing, settings ?? new DepotSettings(new FakePluginStorage()), saveAll ?? (() => (true, null)));

    private static void _Show(Avalonia.Controls.Control control)
    {
        var window = new Window { Content = control };
        window.Show();
        window.UpdateLayout();
    }
}
