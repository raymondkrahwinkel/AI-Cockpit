extern alias backend;

using System.Text.Json.Nodes;
using Cockpit.Infrastructure.Diagrams;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using NSubstitute;
using DiagramMcpTools = backend::Cockpit.Plugin.Diagram.DiagramMcpTools;

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

    private static (DiagramMcpTools Tools, DiagramAccessRegistry Registry, List<ConsentRequest> Asked) _Build(string? source = null)
    {
        // Source is a raw string literal, so it carries the checkout's line endings (core.autocrlf on Windows).
        source ??= Source.ReplaceLineEndings("\n");
        var registry = new DiagramAccessRegistry();
        var asked = new List<ConsentRequest>();
        var host = Substitute.For<ICockpitHost, IWindowProbe>();
        // NSubstitute defaults an unconfigured string-returning member to "", not null — leaving this unset would
        // make `host.CurrentMcpCallerPaneId ?? session` pick "" over the caller-supplied session on every test.
        host.CurrentMcpCallerPaneId.Returns((string?)null);
        host.RequestConsentAsync(Arg.Do<ConsentRequest>(asked.Add))
            .Returns(new ConsentDecision(ConsentOutcome.Approved));
        registry.SurfaceOpened("diagram-1", Diagram, source);
        return (new DiagramMcpTools(host, registry, new backend::Cockpit.Plugin.Diagram.DiagramSettings(new FakePluginStorage()), TestChannel.Desktop(host, diagrams: registry)), registry, asked);
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
}
