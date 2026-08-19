using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Infrastructure.Whiteboard;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Diagram.Tests;

// The cockpit-whiteboard tools (AC-823): reading a surface is gated behind its own Approve/Deny, coupling is
// one-agent-per-surface, coupling on its own grants nothing, and the read consent text names a screenshot (not a
// diagram source, AC-810's text). Since AC-854 there is a second capability: placing an object is asked separately
// — a session that already reads a board is asked again, in its own words — and it only ever adds. Mirrors
// DiagramMcpToolsTests (AC-810).
public class WhiteboardMcpToolsTests
{
    private const string Session = "pane-agent";
    private static readonly byte[] Png = [1, 2, 3, 4];

    private static (WhiteboardMcpTools tools, WhiteboardAccessRegistry registry, ICockpitHost host, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new WhiteboardAccessRegistry();
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        return (new WhiteboardMcpTools(host, registry), registry, host, asked);
    }

    [Fact]
    public async Task ReadWhiteboard_FirstTime_AsksConsent_ThenReturnsTheSnapshotAsItStandsNow()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("image/png", json["mimeType"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Png), json["imageBase64"]!.GetValue<string>());
        Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Equal("board-1", asked[0].Source.PaneId);
        Assert.Contains("Sprint planning", asked[0].Action);
    }

    [Fact]
    public async Task ReadWhiteboard_StampsLastReadAt_SoTheBoardCanShowWhenItWasRead()
    {
        // AC-842's "gelezen 15:11": read_whiteboard must leave a trace the board's own coupling bar can render.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        await tools.ReadWhiteboard(Session, "Sprint planning");

        Assert.NotNull(registry.CouplingOf(Session, "board-1")!.LastReadAt);
    }

    [Fact]
    public async Task PlaceOnWhiteboard_WithReadOnly_AsksAWideningApproval_AndIsRefusedUntilItIsGiven()
    {
        // AC-854's core rule: read was approved under AC-820's promise that an agent never writes to the canvas, so
        // placing something is a new question — a read grant is never quietly widened into a write one.
        var registry = new WhiteboardAccessRegistry();
        var asked = new List<ConsentRequest>();
        var approve = false;
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add))
            .Returns(_ => new ConsentDecision(approve ? ConsentOutcome.Approved : ConsentOutcome.Denied));
        var tools = new WhiteboardMcpTools(host, registry);
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
    public async Task PlaceOnWhiteboard_ReachesTheBoardAsOneObject_AndAsksOnlyOnce()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var placed = new List<WhiteboardPlacement>();
        registry.ObjectPlaced += (_, _, placement) => placed.Add(placement);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        await tools.PlaceOnWhiteboard(Session, "board-1", "rectangle", "Stap 1", x: 30, y: 40);
        await tools.PlaceOnWhiteboard(Session, "board-1", "Sticky-Note", "Stap 2");

        Assert.Single(asked);
        Assert.Equal(2, placed.Count);
        Assert.Equal(new WhiteboardPlacement("rectangle", "Stap 1", 30, 40, 120, 80), placed[0]);
        Assert.Equal("stickynote", placed[1].Shape);
        Assert.True(registry.CouplingOf(Session, "board-1")!.CanRead); // write implies read
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
    public async Task PlaceOnWhiteboard_WithAShapeTheBoardDoesNotHave_IsRefusedWithoutAsking()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        var json = JsonNode.Parse(await tools.PlaceOnWhiteboard(Session, "board-1", "hexagon", "Stap 1"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not a shape", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
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
    public async Task ReadConsentText_NoLongerPromisesThatAnAgentCannotDraw()
    {
        // AC-820/AC-823 promised the operator "writing to a whiteboard is not offered to agents at all". AC-854
        // makes that untrue, so the promise must be gone from the prompt rather than quietly outlived.
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        await tools.ReadWhiteboard(Session, "Sprint planning");

        Assert.Equal("whiteboard.read", asked[0].Scope);
        Assert.DoesNotContain("not offered to agents", asked[0].Action);
        Assert.Contains("separate question", asked[0].Action);
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
        var json = JsonNode.Parse(await tools.ReadWhiteboard("victim-pane", "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Null(json["imageBase64"]);
    }

    [Fact]
    public async Task ReadWhiteboard_WhenDenied_ReturnsError_AndDoesNotCouple()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Null(registry.CouplingOf(Session, "board-1"));
    }

    [Fact]
    public async Task ReadWhiteboard_UnknownSurface_ReturnsError_WithoutAsking()
    {
        var (tools, _, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "ghost"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("No such whiteboard", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ReadWhiteboard_WhenSurfaceCoupledToAnotherAgent_IsRefused_WithoutAsking_AndWithoutException()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant("other-agent", "board-1");

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("another agent", json["error"]!.GetValue<string>());
        Assert.Empty(asked);
    }

    [Fact]
    public void ListWhiteboards_ReturnsOpenSurfaces_WithTheReadFlag()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant(Session, "board-1");
        registry.SurfaceOpened("board-2", "Retro board", Png);

        var json = JsonNode.Parse(tools.ListWhiteboards(Session));

        Assert.True(json!["ok"]!.GetValue<bool>());
        var names = json["whiteboards"]!.AsArray().Select(w => w!["name"]!.GetValue<string>()).ToList();
        Assert.Equivalent(new object[] { "Sprint planning", "Retro board" }, names);
        var coupled = json["whiteboards"]!.AsArray().First(w => w!["name"]!.GetValue<string>() == "Sprint planning");
        Assert.True(coupled!["canRead"]!.GetValue<bool>());
        Assert.False(coupled["canPlace"]!.GetValue<bool>()); // read alone is never write (AC-854)
        var uncoupled = json["whiteboards"]!.AsArray().First(w => w!["name"]!.GetValue<string>() == "Retro board");
        Assert.False(uncoupled!["canRead"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WhenAnotherAgentTakesTheSurfaceWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Any<ConsentRequest>())
            .Returns(_ =>
            {
                registry.Grant("someone-else", "board-1"); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new WhiteboardMcpTools(host, registry);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no longer available", json["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadWhiteboard_WithAnEmptySnapshot_StillSucceeds_ReturningNoImageData()
    {
        // Mirrors DiagramMcpTools.ReadDiagram defaulting a missing source to "" rather than erroring.
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", []);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("", json["imageBase64"]!.GetValue<string>());
    }

    // ---- open_whiteboard (AC-835): the agent asks for a board of its own ----

    [Fact]
    public async Task OpenWhiteboard_WhenApproved_RequestsTheWindow_AndCouplesTheCallerOnArrival()
    {
        var (tools, registry, _, asked) = _Build(ConsentOutcome.Approved);
        var requests = new List<WhiteboardOpenRequest>();
        registry.OpenRequested += requests.Add;

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal("whiteboard.open", asked[0].Scope);
        Assert.Equal(ConsentRisk.Dangerous, asked[0].Risk);
        Assert.Contains("Sprint planning", asked[0].Action);

        var request = Assert.Single(requests);
        Assert.Equal(Session, request.SessionId);
        registry.SurfaceOpened(request.SurfaceId, request.Name, Png);
        var coupling = registry.CouplingOf(Session, request.SurfaceId);
        Assert.NotNull(coupling);
        Assert.False(coupling!.CanRead);
        Assert.False(coupling.CanWrite);
    }

    [Fact]
    public async Task OpenWhiteboard_WhenDenied_OpensNothing_AndSaysSo()
    {
        var (tools, registry, _, _) = _Build(ConsentOutcome.Denied);
        var requests = new List<WhiteboardOpenRequest>();
        registry.OpenRequested += requests.Add;

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not approved", json["error"]!.GetValue<string>());
        Assert.Empty(requests);
        Assert.Empty(registry.ListSurfaces(Session));
    }

    [Fact]
    public async Task OpenWhiteboard_CouplesTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var (tools, registry, host, _) = _Build(ConsentOutcome.Approved);
        var requests = new List<WhiteboardOpenRequest>();
        registry.OpenRequested += requests.Add;

        host.CurrentMcpCallerPaneId.Returns("cockpit-assistant"); // the assistant is a caller like any other (AC-835)
        await tools.OpenWhiteboard("some-other-pane", "Sprint planning");

        var request = Assert.Single(requests);
        Assert.Equal("cockpit-assistant", request.SessionId);
        registry.SurfaceOpened(request.SurfaceId, request.Name, Png);
        Assert.NotNull(registry.CouplingOf("cockpit-assistant", request.SurfaceId));
        Assert.Null(registry.CouplingOf("some-other-pane", request.SurfaceId));
    }

    [Fact]
    public async Task OpenWhiteboard_WithNothingListening_SaysSo_RatherThanClaimingAWindowOpened()
    {
        var (tools, _, _, _) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("diagram plugin", json["error"]!.GetValue<string>());
    }

}
