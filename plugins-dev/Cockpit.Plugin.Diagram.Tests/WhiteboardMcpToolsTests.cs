extern alias backend;

using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using ModelContextProtocol.Protocol;
using NSubstitute;
using WhiteboardMcpTools = backend::Cockpit.Plugin.Diagram.WhiteboardMcpTools;

namespace Cockpit.Plugin.Diagram.Tests;

// The cockpit-whiteboard tools (AC-823): reading a surface is gated behind its own Approve/Deny, coupling is
// one-agent-per-surface, and the read consent text names a screenshot (AC-810's text is a diagram source). Since
// AC-854, placing an object is a second, separately-asked capability — it only ever adds.
[Collection("avalonia")]
public class WhiteboardMcpToolsTests
{
    private const string Session = "pane-agent";
    private static readonly byte[] Png = [1, 2, 3, 4];

    private static (WhiteboardMcpTools tools, WhiteboardAccessRegistry registry, ICockpitHost host, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new WhiteboardAccessRegistry();
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new WhiteboardMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, whiteboards: registry)), registry, host, asked);
    }

    // AC-1007: the image travels as its own content block, not a base64 field a text-only tool result carries.
    private static JsonNode _Meta(CallToolResult result) => JsonNode.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text)!;

    [Fact]
    public async Task ReadWhiteboard_FirstTime_AsksConsent_ThenReturnsTheSnapshotAsItStandsNow()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        var result = await tools.ReadWhiteboard(Session, "Sprint planning");
        var json = _Meta(result);

        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal("image/png", json["mimeType"]!.GetValue<string>());
        Assert.Null(json["imageBase64"]);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Png, image.DecodedData.ToArray());
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Equal("board-1", asked[0].Source.PaneId);
        Assert.Contains("Sprint planning", asked[0].Action);
    }

    [Fact]
    public async Task PlaceOnWhiteboard_WithReadOnly_AsksAWideningApproval_AndIsRefusedUntilItIsGiven()
    {
        // AC-854's core rule: read was approved under AC-820's promise that an agent never writes to the canvas, so
        // placing something is a new question — a read grant is never quietly widened into a write one.
        var registry = new WhiteboardAccessRegistry();
        var asked = new List<ConsentRequest>();
        var approve = false;
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add))
            .Returns(_ => new ConsentDecision(approve ? ConsentOutcome.Approved : ConsentOutcome.Denied));
        var tools = new WhiteboardMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, whiteboards: registry));
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant(Session, "board-1", WhiteboardCapability.Read);

        var denied = JsonNode.Parse(await tools.PlaceOnWhiteboard(Session, "board-1", "stickynote", "Idee"));

        Assert.False(denied!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", denied["error"]!.GetValue<string>());
        Assert.False(registry.CouplingOf(Session, "board-1")!.CanWrite);
        Assert.Equal("whiteboard.write", asked[0].Scope);
        Assert.Contains("now wants to draw on it", asked[0].Title);
        Assert.Contains("Idee", asked[0].Action);

        approve = true;
        var placed = JsonNode.Parse(await tools.PlaceOnWhiteboard(Session, "board-1", "stickynote", "Idee"));

        Assert.True(placed!["ok"]!.GetValue<bool>());
        Assert.NotNull(placed["objectId"]);
        Assert.True(registry.CouplingOf(Session, "board-1")!.CanWrite);
    }

    [Fact]
    public async Task EraseWhiteboardObject_RefusesAnythingTheAgentDidNotPlaceItself()
    {
        // The operator's own strokes and shapes are unknown to the registry — an agent naming one gets a refusal
        // that says so, and nothing on the board moves (AC-854).
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        var erased = new List<string>();
        registry.ObjectErased += (_, objectId) => erased.Add(objectId);

        var placed = JsonNode.Parse(await tools.PlaceOnWhiteboard(Session, "board-1", "rectangle", "Stap 1"));
        var mine = placed!["objectId"]!.GetValue<string>();

        var refused = JsonNode.Parse(await tools.EraseWhiteboardObject(Session, "board-1", "an-object-the-operator-drew"));
        Assert.False(refused!["ok"]!.GetValue<bool>());
        Assert.Contains("never the operator's work", refused["error"]!.GetValue<string>());
        Assert.Empty(erased);

        var ok = JsonNode.Parse(await tools.EraseWhiteboardObject(Session, "board-1", mine));
        Assert.True(ok!["ok"]!.GetValue<bool>());
        Assert.Equal(mine, Assert.Single(erased));
    }

    [Fact]
    public async Task PlaceOnWhiteboard_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var (tools, registry, host, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant("victim-pane", "board-1", WhiteboardCapability.Write);

        host.CurrentMcpCallerPaneId.Returns("attacker-pane");
        var json = JsonNode.Parse(await tools.PlaceOnWhiteboard("victim-pane", "board-1", "rectangle", "boo"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConsentText_NamesAScreenshot_NotADiagramSource()
    {
        // AC-823's deviation from AC-810: the payload is an image, so the prompt must say so explicitly.
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        await tools.ReadWhiteboard(Session, "Sprint planning");

        Assert.Contains("screenshot", asked[0].Action, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("image", asked[0].Action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagram", asked[0].Action, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", asked[0].Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Coupling_OnItsOwn_GrantsNoCapability()
    {
        // AC-823 DoD, same requirement as AC-810: coupling without the capability granted is a real, visible state.
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        registry.Couple(Session, "board-1");

        var coupling = registry.CouplingOf(Session, "board-1");
        Assert.NotNull(coupling);
        Assert.False(coupling!.CanRead);
        Assert.True(registry.IsCoupledByAnother("someone-else", "board-1"));
    }

    [Fact]
    public async Task ReadWhiteboard_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        // Hardening (AC-89 pattern), same as DiagramMcpTools/TerminalMcpTools.
        var (tools, registry, host, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant("victim-pane", "board-1");

        host.CurrentMcpCallerPaneId.Returns("attacker-pane");
        var result = await tools.ReadWhiteboard("victim-pane", "Sprint planning");
        var json = _Meta(result);

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Single(result.Content);
    }

    [Fact]
    public async Task ReadWhiteboard_WhenDenied_ReturnsError_AndDoesNotCouple()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        var json = _Meta(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Null(registry.CouplingOf(Session, "board-1"));
    }

    [Fact]
    public async Task ReadWhiteboard_WhenSurfaceCoupledToAnotherAgent_IsRefused_WithoutAsking_AndWithoutException()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant("other-agent", "board-1");

        var json = _Meta(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task WhenAnotherAgentTakesTheSurfaceWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ =>
            {
                registry.Grant("someone-else", "board-1"); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new WhiteboardMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, whiteboards: registry));

        var json = _Meta(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.Contains("no longer available", json["error"]!.GetValue<string>());
    }

    // ---- open_whiteboard (AC-835, direct path since AC-891): the agent asks for a board of its own ----

    [Fact]
    public async Task OpenWhiteboard_WhenApproved_AsksTheOperator_ThenOpensTheWindowDirectly()
    {
        var (tools, _, host, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("whiteboard.open", asked[0].Scope);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Contains("Sprint planning", asked[0].Action);

        var surfaceId = json["id"]!.GetValue<string>();
        await host.Received(1).ShowDialogAsync("Sprint planning", Arg.Any<Func<Control>>(),
            $"whiteboard.document.{surfaceId}", Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OpenWhiteboard_WhenDenied_OpensNothing_AndSaysSo()
    {
        var (tools, _, host, asked) = _Build(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Single(asked);
        await host.DidNotReceive().ShowDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(),
            Arg.Any<string>(), Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OpenWhiteboard_WithSkipWhiteboardConsent_OpensWithoutAsking()
    {
        // AC-948: the plugin's own opt-out, off by default — on, this surface's consent request never happens.
        var registry = new WhiteboardAccessRegistry();
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var settings = new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()) { SkipWhiteboardConsent = true };
        var tools = new WhiteboardMcpTools(host, registry, settings, TestChannel.Desktop(host, whiteboards: registry));

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        await host.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>());
    }

    [Fact]
    public async Task OpenWhiteboard_WithSkipWhiteboardConsentOff_StillAsks()
    {
        // AC-948 DoD: a fresh install (flag off) keeps asking every time — nothing about today's behaviour changes.
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
    }
}
