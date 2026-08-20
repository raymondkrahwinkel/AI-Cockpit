using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Core.Consent;
using Cockpit.Infrastructure.Wireframe;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

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
        var builtHost = Substitute.For<ICockpitHost>();
        builtHost.CurrentMcpCallerPaneId.Returns((string?)null);
        builtHost.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add)).Returns(new ConsentDecision(outcome));
        host = builtHost;
        return (new WireframeMcpTools(builtHost, registry, new DiagramSettings(new FakePluginStorage())), registry, asked);
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
    public async Task ReadWireframe_HandsBackTheComponentsWithTheIdsTheEditToolsTake()
    {
        var (tools, _, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        var components = json!["components"]!.AsArray();
        Assert.Equal(13, components.Count);
        Assert.Equal(WireframeScreens.Screen, components[0]!["id"]!.GetValue<string>());
        Assert.Equal("screen", components[0]!["type"]!.GetValue<string>());
        Assert.Equal(WireframeScreens.SaveButton, components[12]!["id"]!.GetValue<string>());
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

        var json = JsonNode.Parse(await tools.AddComponent(Session, Name, WireframeScreens.Group, "input", "Telefoonnummer"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal("added input \"Telefoonnummer\"", json["changed"]!.GetValue<string>());
        Assert.Equal(14, json["components"]!.AsArray().Count);
        Assert.Contains("add input \"Telefoonnummer\"", Assert.Single(asked).Action, StringComparison.Ordinal);

        var second = JsonNode.Parse(await tools.SetComponentText(Session, Name, WireframeScreens.SaveButton, "Bewaren"));
        Assert.True(second!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
        Assert.Equal(2, registry.History(SurfaceId).Count);
    }

    [Fact]
    public async Task SetComponentModifier_AFlag_TurnsItOnWithoutAValue()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.AccountItem, "selected"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("selected #account", registry.PeekText(SurfaceId));
        Assert.Contains("set selected on component", Assert.Single(asked).Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetComponentModifier_AFlag_WithClear_TakesItOff()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.GeneralItem, "selected", clear: true));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.DoesNotContain("selected", registry.PeekText(SurfaceId)!.Split('\n')[4]);
    }

    [Fact]
    public async Task SetComponentModifier_AValue_SetsIt_AndClearRemovesIt()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);

        var set = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.EmailField, "value", "raymond@example.com"));
        Assert.True(set!["ok"]!.GetValue<bool>());
        Assert.Contains("value:\"raymond@example.com\"", registry.PeekText(SurfaceId));

        var cleared = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.EmailField, "value", clear: true));
        Assert.True(cleared!["ok"]!.GetValue<bool>());
        Assert.Equal("        input \"E-mailadres\" #email", registry.PeekText(SurfaceId)!.Split('\n')[9]);
    }

    [Fact]
    public async Task SetComponentModifier_WithAKeywordTheFormatDoesNotHave_IsRefusedWithoutAsking()
    {
        var (tools, _, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.SaveButton, "bold"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("not a modifier this format has", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task SetComponentModifier_WithNoMeaningOnThisComponent_IsRefused()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, WireframeScreens.NameField, "primary"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("no meaning", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(WireframeScreens.Settings, registry.PeekText(SurfaceId));
    }

    [Fact]
    public async Task SetComponentModifier_Goto_QuotesATitleWithASpace_UnconditionallyUnlikeValue()
    {
        // AC-902: goto: is quoted unconditionally, unlike value:, because a screen title almost always carries a
        // space — value:'s int.TryParse check would leave this one unquoted, splitting it into two tokens.
        const string source = """
            screen "Aanmelden" #login
              button "Verder" primary #go

            screen "Wachtwoord vergeten" #forgot
              label "Vul je e-mailadres in" #hint
            """;
        var built = _Build(ConsentOutcome.Approved);
        built.registry.SurfaceOpened(SurfaceId, Name, source);
        var (tools, registry, _) = built;

        var json = JsonNode.Parse(await tools.SetComponentModifier(Session, Name, "go", "goto", "Wachtwoord vergeten"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("goto:\"Wachtwoord vergeten\"", registry.PeekText(SurfaceId));
    }

    [Fact]
    public async Task ReadWireframe_AGotoField_ResolvesToTheTargetScreensId()
    {
        var built = _Build(ConsentOutcome.Approved);
        built.registry.SurfaceOpened(SurfaceId, Name, WireframeScreens.TwoScreensWithFlow);
        var (tools, _, _) = built;

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        var components = json!["components"]!.AsArray();
        var submit = components.Single(component => component!["id"]!.GetValue<string>() == WireframeScreens.LoginSubmit);
        Assert.Equal(WireframeScreens.SignupScreen, submit!["goto"]!.GetValue<string>());
        var withoutFlow = components.Single(component => component!["id"]!.GetValue<string>() == WireframeScreens.SignupSubmit);
        Assert.Null(withoutFlow!["goto"]);
    }

    [Fact]
    public async Task ChangeComponentType_KeepsThePlaceTheTextAndTheChildren()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ChangeComponentType(Session, Name, WireframeScreens.NameField, "select"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("select \"Profielnaam\" value:\"Raymond\" #name", registry.PeekText(SurfaceId));
        Assert.Contains("change component", Assert.Single(asked).Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangeComponentType_WhenTheNewTypeCannotCarryItsChildren_IsRefused()
    {
        var (tools, registry, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ChangeComponentType(Session, Name, WireframeScreens.Group, "label"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("carries no components of its own", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
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

    [Fact]
    public async Task OpenWireframe_RefusesASourceTheFormatCannotRead_WithoutAskingAnyone()
    {
        var (tools, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", "button \"Los\""));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Empty(asked);
    }

    // ---- open_wireframe (AC-835, direct path since AC-891): the agent asks for a window of its own ----

    [Fact]
    public async Task OpenWireframe_WhenApproved_AsksTheOperator_ThenOpensTheWindowDirectly()
    {
        var (tools, _, asked) = _BuildWithHost(ConsentOutcome.Approved, out var host);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", WireframeScreens.Settings));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);

        var surfaceId = json["id"]!.GetValue<string>();
        await host.Received(1).ShowDialogAsync("Nieuw scherm", Arg.Any<Func<Control>>(),
            $"wireframe.document.{surfaceId}", Arg.Any<double>(), Arg.Any<double>());
    }

    [Fact]
    public async Task OpenWireframe_WithSkipWireframeConsent_OpensWithoutAsking()
    {
        // AC-948: the plugin's own opt-out, off by default — on, this surface's consent request never happens.
        var registry = new WireframeAccessRegistry();
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        var settings = new DiagramSettings(new FakePluginStorage()) { SkipWireframeConsent = true };
        var tools = new WireframeMcpTools(host, registry, settings);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", WireframeScreens.Settings));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        await host.DidNotReceive().RequestConsentAsync(Arg.Any<ConsentRequest>());
    }

    [Fact]
    public async Task OpenWireframe_WithSkipWireframeConsentOff_StillAsks()
    {
        // AC-948 DoD: a fresh install (flag off) keeps asking every time — nothing about today's behaviour changes.
        var (tools, _, asked) = _Build(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.OpenWireframe(Session, "Nieuw scherm", WireframeScreens.Settings));
        Dispatcher.UIThread.RunJobs();

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Single(asked);
    }

    // ---- A document of several screens (AC-901) ----

    [Fact]
    public async Task ReadWireframe_SaysWhichScreenEveryComponentIsIn()
    {
        var (tools, registry, _) = _Build(ConsentOutcome.Approved);
        registry.SurfaceOpened(SurfaceId, Name, WireframeScreens.TwoScreens);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        Assert.Equal(
            [WireframeScreens.LoginScreen, WireframeScreens.SignupScreen],
            json!["screens"]!.AsArray().Select(screen => screen!["id"]!.GetValue<string>()));
        var submit = json["components"]!.AsArray()
            .Single(component => component!["id"]!.GetValue<string>() == WireframeScreens.SignupSubmit);
        Assert.Equal(WireframeScreens.SignupScreen, submit!["screen"]!.GetValue<string>());
        Assert.Equal("Registreren", submit["screenTitle"]!.GetValue<string>());
    }

    [Fact]
    public async Task AddScreen_AddsOneBesideTheOnesAlreadyThere_AndSaysSoInThePrompt()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.AddScreen(Session, Name, "Aanmelden"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Equal(2, json["screens"]!.AsArray().Count);
        Assert.Contains("add a screen \"Aanmelden\"", Assert.Single(asked).Action, StringComparison.Ordinal);
        Assert.Contains("screen \"Aanmelden\"", registry.PeekText(SurfaceId), StringComparison.Ordinal);
    }

    // ---- Viewport (AC-915) ----

    [Fact]
    public async Task ReadWireframe_WithNoViewportLine_ReportsDesktopAndItsSize()
    {
        var (tools, _, _) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.ReadWireframe(Session, Name));

        var viewport = json!["viewport"]!;
        Assert.Equal("desktop", viewport["name"]!.GetValue<string>());
        Assert.Equal(960, viewport["width"]!.GetValue<double>());
        Assert.Equal(640, viewport["height"]!.GetValue<double>());
    }

    [Fact]
    public async Task SetWireframeViewport_AppliesStraightAway_AndReadBackReportsIt()
    {
        var (tools, registry, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetWireframeViewport(Session, Name, "mobile"));

        Assert.True(json!["ok"]!.GetValue<bool>());
        Assert.Contains("viewport mobile", registry.PeekText(SurfaceId), StringComparison.Ordinal);
        Assert.Contains("set the viewport to mobile", Assert.Single(asked).Action, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetWireframeViewport_WithAnUnknownName_IsRefusedWithoutAskingAnyone()
    {
        var (tools, _, asked) = _Open(ConsentOutcome.Approved);

        var json = JsonNode.Parse(await tools.SetWireframeViewport(Session, Name, "phablet"));

        Assert.False(json!["ok"]!.GetValue<bool>());
        Assert.Contains("desktop, tablet or mobile", json["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(asked);
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
