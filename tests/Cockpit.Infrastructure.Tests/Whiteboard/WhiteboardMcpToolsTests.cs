using System.Text.Json.Nodes;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Whiteboard;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Whiteboard;

/// <summary>
/// The cockpit-whiteboard tools (AC-823): reading a surface is gated behind its own Approve/Deny, coupling is
/// one-agent-per-surface, coupling on its own grants nothing, the consent text names a screenshot (not a diagram
/// source, AC-810's text), and there is no edit capability at all. Mirrors DiagramMcpToolsTests (AC-810).
/// </summary>
public class WhiteboardMcpToolsTests
{
    private const string Session = "pane-agent";
    private static readonly byte[] Png = [1, 2, 3, 4];

    private static (WhiteboardMcpTools tools, WhiteboardAccessRegistry registry, IConsentBroker broker, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new WhiteboardAccessRegistry();
        var asked = new List<ConsentRequest>();
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(outcome));
        return (new WhiteboardMcpTools(registry, broker), registry, broker, asked);
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
        var (tools, registry, _, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        registry.Grant("victim-pane", "board-1");

        McpRequestContext.Set("attacker-pane");
        try
        {
            var json = JsonNode.Parse(await tools.ReadWhiteboard("victim-pane", "Sprint planning"));

            Assert.False(json!["ok"]!.GetValue<bool>());
            Assert.Contains("another agent", json["error"]!.GetValue<string>());
            Assert.Null(json["imageBase64"]);
        }
        finally
        {
            McpRequestContext.Set(null);
        }
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
    public async Task ReadWhiteboard_WithNoConsentBroker_FailsClosed()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        var tools = new WhiteboardMcpTools(registry, consent: null);

        var json = JsonNode.Parse(await tools.ReadWhiteboard(Session, "Sprint planning"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Null(registry.CouplingOf(Session, "board-1"));
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
        var uncoupled = json["whiteboards"]!.AsArray().First(w => w!["name"]!.GetValue<string>() == "Retro board");
        Assert.False(uncoupled!["canRead"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WhenAnotherAgentTakesTheSurfaceWhileTheOperatorDecides_TheRefusalIsAnErrorNotAnException()
    {
        var registry = new WhiteboardAccessRegistry();
        registry.SurfaceOpened("board-1", "Sprint planning", Png);
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                registry.Grant("someone-else", "board-1"); // slipped in while we asked
                return new ConsentDecision(ConsentOutcome.Approved);
            });
        var tools = new WhiteboardMcpTools(registry, broker);

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
}
