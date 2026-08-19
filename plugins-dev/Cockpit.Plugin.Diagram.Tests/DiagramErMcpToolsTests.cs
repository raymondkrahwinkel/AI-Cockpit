using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;

namespace Cockpit.Plugin.Diagram.Tests;

// AC-899: the agent's side of erDiagram editing — its own tools rather than the flowchart five, because an entity
// has no label and a relationship cannot do without two cardinalities and a verb. Same consent and hold gates.
public class DiagramErMcpToolsTests
{
    private const string Session = "pane-agent";
    private const string Diagram = "Bestellingen";

    private const string Source = """
        erDiagram
            CUSTOMER ||--o{ ORDER : "places"
            CUSTOMER {
                string name
            }
        """;

    private static (DiagramMcpTools Tools, DiagramAccessRegistry Registry, List<ConsentRequest> Asked) _Build(string source = Source)
    {
        var registry = new DiagramAccessRegistry();
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add))
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
        registry.SurfaceOpened("diagram-1", Diagram, source);
        return (new DiagramMcpTools(host, registry), registry, asked);
    }

    private static JsonNode Reply(string json) => JsonNode.Parse(json)!;

    [Fact]
    public async Task AddEntity_AppliesStraightAway_UnderTheSameEditConsentAsEditDiagram()
    {
        var (tools, registry, asked) = _Build();

        var reply = Reply(await tools.AddEntity(Session, Diagram, "INVOICE"));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.Equal("diagram.edit", Assert.Single(asked).Scope);
        Assert.EndsWith("\n    INVOICE {\n    }", registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task SetAttribute_ChangesOneLine_AndLeavesTheRestOfTheDiagramAlone()
    {
        var (tools, registry, _) = _Build();

        await tools.SetAttribute(Session, Diagram, "CUSTOMER", "id", "int", "PK");

        Assert.Equal("""
            erDiagram
                CUSTOMER ||--o{ ORDER : "places"
                CUSTOMER {
                    string name
                    int id PK
                }
            """.ReplaceLineEndings("\n"), registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task RelateEntities_WithACardinalityThatIsNotOne_IsRefusedWithoutAskingTheOperator()
    {
        var (tools, registry, asked) = _Build();

        var reply = Reply(await tools.RelateEntities(Session, Diagram, "ORDER", "CUSTOMER", "several", "one", "belongs to"));

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("zero-or-more", reply["error"]!.GetValue<string>());
        Assert.Empty(asked);
        Assert.Equal(Source.ReplaceLineEndings("\n"), registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task RelateEntities_WritesTheCrowsFootPairAndTheLabel()
    {
        var (tools, registry, _) = _Build();

        var reply = Reply(await tools.RelateEntities(Session, Diagram, "ORDER", "CUSTOMER", "zero-or-more", "one", "belongs to"));

        Assert.True(reply["ok"]!.GetValue<bool>());
        Assert.EndsWith("\n    ORDER }o--|| CUSTOMER : \"belongs to\"", registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task AnErTool_OnAFlowchart_IsRefused_NamingTheToolsThatDoWorkThere()
    {
        var (tools, registry, _) = _Build("flowchart LR\n    A[\"Start\"]");

        var reply = Reply(await tools.AddEntity(Session, Diagram, "CUSTOMER"));

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("add_node", reply["error"]!.GetValue<string>());
        Assert.Equal("flowchart LR\n    A[\"Start\"]", registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task AFlowchartTool_OnAnErDiagram_IsRefused_NamingTheToolsThatDoWorkThere()
    {
        var (tools, registry, _) = _Build();

        var reply = Reply(await tools.AddNode(Session, Diagram, "INVOICE", "Invoice"));

        Assert.False(reply["ok"]!.GetValue<bool>());
        Assert.Contains("add_entity", reply["error"]!.GetValue<string>());
        Assert.Equal(Source.ReplaceLineEndings("\n"), registry.PeekText("diagram-1"));
    }

    [Fact]
    public async Task AnAttributeEditOnAnEntityTheOperatorIsHolding_IsRefused_WhileAnotherEntityStillEdits()
    {
        var (tools, registry, _) = _Build();
        registry.HoldObject("diagram-1", "CUSTOMER");

        var refused = Reply(await tools.SetAttribute(Session, Diagram, "CUSTOMER", "id", "int", "PK"));
        var landed = Reply(await tools.SetAttribute(Session, Diagram, "ORDER", "id", "int", "PK"));

        Assert.False(refused["ok"]!.GetValue<bool>());
        Assert.Contains("operator is editing", refused["error"]!.GetValue<string>());
        Assert.True(landed["ok"]!.GetValue<bool>());
        Assert.Contains("string name", registry.PeekText("diagram-1"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveEntity_ReportsWhatWentWithIt_AndIsJournaledForARevert()
    {
        var (tools, registry, _) = _Build();

        var reply = Reply(await tools.RemoveEntity(Session, Diagram, "CUSTOMER"));

        Assert.Contains("1 relationship", reply["changed"]!.GetValue<string>());
        var entry = Assert.Single(registry.History("diagram-1"));
        Assert.Equal(DiagramHandEditKind.RemoveEntity, entry.Kind);
        Assert.Null(registry.Revert("diagram-1", entry.Id));
        Assert.Contains("string name", registry.PeekText("diagram-1"), StringComparison.Ordinal);
    }
}
