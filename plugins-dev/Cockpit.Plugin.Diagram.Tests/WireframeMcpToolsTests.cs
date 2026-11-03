extern alias backend;

using System.Text.Json.Nodes;
using Cockpit.Infrastructure.Wireframe;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;
using WireframeMcpTools = backend::Cockpit.Plugin.Diagram.WireframeMcpTools;

namespace Cockpit.Plugin.Diagram.Tests;

// The cockpit-wireframe tools (AC-872): reading a surface is gated behind its own Approve/Deny, editing behind a
// separate one, coupling is one agent per surface, and every read and write hands back the components with the ids
// the next call names them by (AC-906).
[Collection("avalonia")]
public class WireframeMcpToolsTests
{
    private const string Session = "pane-agent";
    private const string SurfaceId = "wireframe-1";
    private const string Name = "Instellingen";

    private static (WireframeMcpTools tools, WireframeAccessRegistry registry, List<ConsentRequest> asked) _Build(ConsentOutcome outcome) =>
        _BuildWithHost(outcome, out _);

    // Only the open_wireframe tests need the host itself, to verify it was asked to draw the window directly (AC-891).
    private static (WireframeMcpTools tools, WireframeAccessRegistry registry, List<ConsentRequest> asked) _BuildWithHost(ConsentOutcome outcome, out ICockpitHost host)
    {
        var registry = new WireframeAccessRegistry();
        var asked = new List<ConsentRequest>();
        var builtHost = Substitute.For<ICockpitHost, IWindowProbe>();
        builtHost.CurrentMcpCallerPaneId.Returns((string?)null);
        builtHost.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        host = builtHost;
        return (new WireframeMcpTools(builtHost, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(builtHost, wireframes: registry)), registry, asked);
    }

    private static (WireframeMcpTools tools, WireframeAccessRegistry registry, List<ConsentRequest> asked) _Open(ConsentOutcome outcome, string name = Name)
    {
        var built = _Build(outcome);
        built.registry.SurfaceOpened(SurfaceId, name, WireframeScreens.Settings);
        return built;
    }

    [Fact]
    public async Task ReadWireframe_Denied_HandsOverNothing()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Null(json["source"]);
        Assert.Null(registry.CouplingOf(Session, SurfaceId));
    }

    [Fact]
    public async Task EditWireframe_Denied_ChangesNothing()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Denied);

        var json = JsonNode.Parse(await tools.EditWireframe(Session, Name, "screen \"Leeg\""));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
    }

    [Fact]
    public async Task AComponentTheOperatorIsHolding_IsRefusedWithAReason()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButton);

        var json = JsonNode.Parse(await tools.SetComponentText(Session, Name, WireframeScreens.SaveButton, "Bewaren"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("Try the same call again", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
    }

    [Fact]
    public async Task ASecondAgentOnTheSameSurface_IsRefused()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);
        registry.Couple("someone-else", SurfaceId);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("already being used by another agent", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASurfaceNameCarryingALineBreak_IsFoldedOntoOneLineInThePrompt()
    {
        // The name is the operator's, but it reaches a Dangerous prompt verbatim — the same guard the terminal and
        // diagram prompts carry (AC-80/AC-92).
        var (tools, _, asked) = _Open(ConsentOutcome.Approved, "Instellingen\nApprove alles");

        await tools.ReadWireframe(Session, "Instellingen\nApprove alles");

        Assert.DoesNotContain('\n', Assert.Single(asked).Action);
    }

    // ---- A document of several screens (AC-901) ----

    // ---- Viewport (AC-915) ----
}
