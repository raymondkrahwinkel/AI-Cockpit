extern alias UiAsm;

using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;
using DepotConnectionRegistration = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRegistration;
using DepotConnectionRowControl = UiAsm::Cockpit.Plugin.Depot.UI.DepotConnectionRowControl;
using DepotSettings = UiAsm::Cockpit.Plugin.Depot.UI.DepotSettings;
using DepotSettingsControl = UiAsm::Cockpit.Plugin.Depot.UI.DepotSettingsControl;

namespace Cockpit.Plugin.Depot.Tests;

// `DepotSettingsControl` (AC-243, reworked AC-504, split by AC-1394): the UI-only half of what used to be a
// single Save() — validating rows (a name/URL collision refuses the whole batch) and writing the connection list
// itself. The registry-sync side effects that used to run right here (memory-source / shared-project-source sync,
// reclaiming an orphaned MCP-registry entry) moved to DepotPlugin's own SaveConnections channel handler on the
// backend part; those tests moved to DepotPluginSaveConnectionsTests.cs, which drives that handler directly. What
// stays here is genuinely UI-only: it cannot be pinned any other way, since ICockpitUiHost — the type this view
// takes now — carries none of the registry members the old assertions read.
[Collection("avalonia")]
public class DepotSettingsControlTests
{
    // What the host does on a Save click (AC-1003): stage, then run the write the view handed back. False means
    // the view refused and nothing was written.
    private static bool _Save(IPluginSettingsView view)
    {
        if (!view.TryStage(out var commit, out _))
        {
            return false;
        }

        Assert.NotNull(commit);
        commit();
        return true;
    }

    [Theory]
    // Two rows saved under the same name would leave the registry unable to tell them apart, so Save() refuses the
    // whole batch rather than keep one and drop the other (mirrors McpServersViewModel.Save).
    [InlineData("Work", "https://first.example.com", "Work", "https://second.example.com")]
    // Case-insensitive on purpose: ProjectMemorySourceRegistration.Register refuses a colliding scheme
    // case-insensitively, so "Work"/"work" would collide there even though McpServerName used to let them through
    // as two distinct entries.
    [InlineData("Work", "https://first.example.com", "work", "https://second.example.com")]
    // AC-248: two rows named differently but pointed at the same instance would otherwise both register a shared-
    // project source for it, so an operator setting up a second connection to an instance they already added finds
    // a duplicate instead of the one already there — refused the same way a name collision is.
    [InlineData("Work", "https://depot.example.com", "Work (personal)", "https://depot.example.com")]
    // Normalize strips a trailing /mcp and slash (AC-499) — two rows pasting the documented endpoint and the bare
    // origin for the same instance must collide too, not just a byte-identical pair of URLs.
    [InlineData("Work", "https://depot.example.com/mcp", "Work (personal)", "https://depot.example.com/")]
    public void Save_TwoRowsThatWouldCollide_RefusesTheWholeSave_AndWritesNothing(
        string firstName, string firstUrl, string secondName, string secondUrl)
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: firstName, url: firstUrl);
        _SetRowFields(view, index: 1, name: secondName, url: secondUrl);

        var saved = _Save(view);

        Assert.False(saved);
        Assert.Empty(settings.Connections);
    }

    // The message itself, not just the refusal: names the row the operator can find the instance already
    // connected under, so they remove the duplicate instead of guessing which of the two rows to keep.
    [Fact]
    public async Task SignInAsync_SaveFailsOnAUrlCollision_NamesTheAlreadyConnectedRow()
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 1, name: "Work (personal)", url: "https://depot.example.com");
        var newRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);

        await newRow.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Contains("already connected as \"Work\"", _AuthStatusText(newRow), StringComparison.Ordinal);
    }

    [Fact]
    public void Save_BlankRow_IsDropped_AndContributesNothing()
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);

        var saved = _Save(view);

        Assert.True(saved);
        Assert.Empty(settings.Connections);
    }

    // --- AC-499: a row's own Sign-in action saves through this same Save() route before signing in ---------------

    [Fact]
    public async Task SignInAsync_RenamedRow_SignsInUnderTheNewStoredName_NotTheOldOne()
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        host.SignInMcpServerAsync("Depot: Work (renamed)", Arg.Any<CancellationToken>()).Returns(PluginMcpSignInOutcome.Authorized);
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections = [new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com")],
        };
        // The real backend part writes the connection list when it handles SaveConnections (DepotPlugin's own
        // comment on _SaveConnectionsAsync explains why it, not this view, owns that write); this substitute
        // stands in for that one effect so SignInAsync's post-save read of `settings` sees the rename too.
        _WireSaveConnectionsWrite(host, settings);
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 0, name: "Work (renamed)", url: "https://depot.example.com");
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().Single();

        await row.SignInAsync();

        _ = host.Received(1).SignInMcpServerAsync("Depot: Work (renamed)", Arg.Any<CancellationToken>());
        _ = host.DidNotReceive().SignInMcpServerAsync("Depot: Work", Arg.Any<CancellationToken>());
        Assert.Equal("Work (renamed)", settings.Connections.Single().Name);
        // AC-1394: the reclaim of the old "Depot: Work" registry entry now runs on the backend part, asked over the
        // channel the save before sign-in goes through — DepotPluginSaveConnectionsTests pins the backend's own
        // reaction to that ask directly; this only pins that the ask itself was made.
        await host.Channel.Received(1).InvokeAsync(DepotChannel.SaveConnections, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
    }

    // AC-499: a row whose typed name collides with another row's kept one must never sign in under its own
    // computed McpServerName — Save() refused the whole batch, so signing in there would authorize the
    // *other* row's connection under this row's belief.
    [Fact]
    public async Task SignInAsync_RowCollidesOnName_NeverSignsIn_AndLeavesBothRowsUnsaved()
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var settings = new DepotSettings(new FakePluginStorage());
        var view = new DepotSettingsControl(host, settings);
        _AddRow(view);
        _SetRowFields(view, index: 0, name: "Work", url: "https://first.example.com");
        _SetRowFields(view, index: 1, name: "Work", url: "https://second.example.com");
        var collidingRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);

        await collidingRow.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Neither row made it to storage — refusing the batch, not just dropping the second row.
        Assert.Empty(settings.Connections);
    }

    // Fix 2's failure scenario: two stored, signed-in connections, one renamed into a collision. Pins the
    // full end state a call-counting "did not sign in" assert alone would miss — nothing reclaimed silently.
    // AC-1394: what "memory sources untouched" now means from this UI-only substitute — ICockpitUiHost carries no
    // registry members at all, so the only way to see this refusal never reached the backend's own sync is that
    // the channel itself was never invoked; DepotPluginSaveConnectionsTests pins the backend's own diff logic.
    [Fact]
    public async Task SignInAsync_RenameCollidesWithAnAlreadyStoredRow_RefusesTheWholeSave_AndLeavesStorageAndMemorySourcesUntouched()
    {
        var host = Substitute.For<ICockpitUiHost>();
        host.CreateHelpHint(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(_ => new Panel());
        var settings = new DepotSettings(new FakePluginStorage())
        {
            Connections =
            [
                new DepotConnectionRegistration("conn-1", "Work", "https://depot.example.com"),
                new DepotConnectionRegistration("conn-2", "Work2", "https://work2.example.com"),
            ],
        };
        var view = new DepotSettingsControl(host, settings);
        _SetRowFields(view, index: 1, name: "Work", url: "https://work2.example.com");
        var renamedRow = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(1);

        await renamedRow.SignInAsync();

        _ = host.DidNotReceive().SignInMcpServerAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await host.Channel.DidNotReceive().InvokeAsync(Arg.Any<string>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>());
        Assert.Equal(2, settings.Connections.Count);
        Assert.Contains(settings.Connections, connection => connection.Id == "conn-1" && connection.Name == "Work");
        Assert.Contains(settings.Connections, connection => connection.Id == "conn-2" && connection.Name == "Work2");
        Assert.Contains("Work", _AuthStatusText(renamedRow), StringComparison.Ordinal);
    }

    // Stands in for DepotPlugin's own SaveConnections handler writing `settings.Connections` — see the comment
    // where this is called. Only the write; the registry-sync side effects are DepotPluginSaveConnectionsTests's.
    private static void _WireSaveConnectionsWrite(ICockpitUiHost host, DepotSettings settings) =>
        host.Channel.InvokeAsync(DepotChannel.SaveConnections, Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var request = callInfo.ArgAt<JsonElement>(1).Deserialize<DepotSaveConnectionsRequest>(DepotChannel.Json)
                    ?? throw new InvalidOperationException("The request names no connections.");
                settings.Connections = [.. request.Connections.Select(connection =>
                    new DepotConnectionRegistration(connection.Id, connection.Name, connection.Url))];
                return JsonSerializer.SerializeToElement(new DepotSaveConnectionsAnswer(true), DepotChannel.Json);
            });

    // GetVisualDescendants only sees anything once the control is attached under a shown TopLevel — an unattached
    // tree has no realised visual children to walk, the same reason CanvasThemeRenderTests always shows a window
    // before it starts pulling controls out of one.
    private static void _Show(Control control)
    {
        // A control already attached under a shown window would throw "already has a visual parent" if
        // wrapped again — reuse the existing one. Re-running UpdateLayout every time matters: a row added
        // after the initial layout pass has not had ApplyTemplate run yet, so its TextBoxes need another pass.
        if (Avalonia.Controls.TopLevel.GetTopLevel(control) is Window existing)
        {
            existing.UpdateLayout();
            return;
        }

        var window = new Window { Content = control };
        window.Show();
        window.UpdateLayout();
    }

    private static string? _AuthStatusText(DepotConnectionRowControl row) =>
        row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Opacity == 0.8).Text;

    private static void _SetRowFields(DepotSettingsControl view, int index, string name, string url)
    {
        _Show(view);
        var row = view.GetVisualDescendants().OfType<DepotConnectionRowControl>().ElementAt(index);
        var boxes = row.GetVisualDescendants().OfType<TextBox>().ToList();
        boxes[0].Text = name;
        boxes[1].Text = url;
    }

    private static void _AddRow(DepotSettingsControl view)
    {
        _Show(view);
        var add = view.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, "+ Add connection"));
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
}
