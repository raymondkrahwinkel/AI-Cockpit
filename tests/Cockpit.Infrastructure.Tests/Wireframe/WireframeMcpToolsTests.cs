using System.Text.Json.Nodes;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Consent;
using Cockpit.Infrastructure.Wireframe;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Wireframe;

/// <summary>
/// The cockpit-wireframe tools (AC-872): reading a surface is gated behind its own Approve/Deny, editing behind a
/// separate one, coupling is one agent per surface, and every write hands back the components with the line numbers
/// the next call needs — they have just moved.
/// </summary>
public class WireframeMcpToolsTests
{
    private const string Session = "pane-agent";
    private const string SurfaceId = "wireframe-1";
    private const string Name = "Instellingen";

    private static (WireframeMcpTools tools, WireframeAccessRegistry registry, List<ConsentRequest> asked) _Build(ConsentOutcome outcome)
    {
        var registry = new WireframeAccessRegistry();
        var asked = new List<ConsentRequest>();
        var broker = Substitute.For<IConsentBroker>();
        broker.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add), Arg.Any<CancellationToken>())
            .Returns(new ConsentDecision(outcome));
        return (new WireframeMcpTools(registry, broker), registry, asked);
    }

    private static (WireframeMcpTools tools, WireframeAccessRegistry registry, List<ConsentRequest> asked) _Open(ConsentOutcome outcome, string name = Name)
    {
        var built = _Build(outcome);
        built.registry.SurfaceOpened(SurfaceId, name, WireframeScreens.Settings);
        return built;
    }

    [Fact]
    public async Task ReadWireframe_FirstTime_AsksConsentThatNamesTheWireframeText_ThenReturnsTheSource()
    {
        var (tools, _, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(WireframeScreens.Settings, json["source"]!.GetValue<string>());
        var request = Assert.Single(asked);
        Assert.Equal(ConsentRisk.Dangerous, request.Risk);
        Assert.Equal(ConsentSourceCatalog.WireframeMcp, request.Source.Label);
        Assert.Contains("read the wireframe text", request.Action, StringComparison.Ordinal);
        Assert.Contains(Name, request.Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadWireframe_HandsBackTheComponentsWithTheLineNumbersTheEditToolsTake()
    {
        var (tools, _, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        var components = json!["components"]!.AsArray();
        Assert.Equal(13, components.Count);
        Assert.Equal(1, components[0]!["line"]!.GetValue<int>());
        Assert.Equal("screen", components[0]!["type"]!.GetValue<string>());
        Assert.Equal(WireframeScreens.SaveButtonLine, components[12]!["line"]!.GetValue<int>());
        Assert.Equal("Opslaan", components[12]!["text"]!.GetValue<string>());
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
    public async Task ReadWireframe_Approved_MarksWhenItWasRead()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);

        await tools.ReadWireframe(Session, Name);

        Assert.NotNull(registry.CouplingOf(Session, SurfaceId)!.LastReadAt);
    }

    [Fact]
    public async Task EditWireframe_AfterReading_AsksAWideningApproval_AndAppliesStraightAway()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);
        await tools.ReadWireframe(Session, Name);

        var json = JsonNode.Parse(await tools.EditWireframe(Session, Name, "screen \"Leeg\""));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("screen \"Leeg\"", registry.PeekText(SurfaceId));
        Assert.Equal(2, asked.Count);
        Assert.Contains("now wants to edit it", asked[1].Title, StringComparison.Ordinal);
        Assert.Equal(ConsentRisk.Dangerous, asked[1].Risk);
        Assert.Contains("edit wireframe", asked[1].Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditWireframe_DescribesTheChangeMechanically_NotFromAnythingTheAgentWrote()
    {
        var (tools, _, asked) = _Open(ConsentOutcome.Approved);

        await tools.EditWireframe(Session, Name, "screen \"Leeg\"");

        Assert.Contains("1 line added, 13 lines removed", Assert.Single(asked).Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditWireframe_WithASourceTheFormatCannotRead_IsRefusedBeforeTheOperatorIsEvenAsked()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.EditWireframe(Session, Name, "screen \"Iets\"\n  button \"Ja\" bold"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.NotEmpty(json["problems"]!.AsArray());
        Assert.Empty(asked);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
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
    public async Task AddComponent_SharesEditsOneApproval_AndAnswersWithTheComponentsAsTheyNowStand()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.AddComponent(Session, Name, WireframeScreens.GroupLine, "input", "Telefoonnummer"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("added input \"Telefoonnummer\"", json["changed"]!.GetValue<string>());
        Assert.Equal(14, json["components"]!.AsArray().Count);
        Assert.Contains("add input \"Telefoonnummer\"", Assert.Single(asked).Action, StringComparison.Ordinal);

        var second = JsonNode.Parse(await tools.SetComponentText(Session, Name, WireframeScreens.SaveButtonLine, "Bewaren"));
        Assert.True(second!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal(2, registry.History(SurfaceId).Count);
    }

    [Fact]
    public async Task AComponentTheOperatorIsHolding_IsRefusedWithAReason()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);
        registry.HoldComponent(SurfaceId, WireframeScreens.SaveButtonLine);

        var json = JsonNode.Parse(await tools.SetComponentText(Session, Name, WireframeScreens.SaveButtonLine, "Bewaren"));

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
    public async Task WithoutAConsentBroker_EveryAccessFailsClosed()
    {
        var registry = new WireframeAccessRegistry();
        registry.SurfaceOpened(SurfaceId, Name, WireframeScreens.Settings);
        var tools = new WireframeMcpTools(registry);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Null(registry.CouplingOf(Session, SurfaceId));
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

    [Fact]
    public async Task OpenWireframe_RefusesASourceTheFormatCannotRead_WithoutAskingAnyone()
    {
        var (tools, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", "button \"Los\""));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task OpenWireframe_Approved_ButNothingDrawsWireframeWindows_SaysSo()
    {
        var (tools, _, _) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", WireframeScreens.Settings));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("draws wireframe windows", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void ListWireframes_NamesTheSurfacesWithoutHandingOverAnythingInThem()
    {
        var (tools, _, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(tools.ListWireframes(Session));

        var listed = Assert.Single(json!["wireframes"]!.AsArray());
        Assert.Equal(SurfaceId, listed!["id"]!.GetValue<string>());
        Assert.False(listed["canRead"]!.GetValue<bool>());
        Assert.False(listed["canEdit"]!.GetValue<bool>());
        Assert.Empty(asked);
    }
}
