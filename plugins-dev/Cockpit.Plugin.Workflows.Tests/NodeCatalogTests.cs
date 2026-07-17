using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The steps the cockpit offers (#69) and how the picker finds them. Deliberately cockpit-shaped: the value is in
/// what only this app can do — start sessions, delegate work, watch what an agent says. A general automation kit
/// already exists, and Raymond runs it.
/// </summary>
public class NodeCatalogTests
{
    [Fact]
    public void ADecision_HasTwoWaysOut_AndTheyAreNamed()
    {
        var decision = NodeCatalog.Find("cockpit.if");

        decision.Should().NotBeNull();
        decision!.Outputs.Should().Equal("true", "false");
        decision.Kind.Should().Be(WorkflowNodeKind.Decision);
    }

    [Fact]
    public void ATrigger_TakesNothingIn()
    {
        NodeCatalog.All.Where(type => type.Kind == WorkflowNodeKind.Trigger)
            .Should().OnlyContain(type => !type.HasInput);
    }

    [Fact]
    public void EveryStep_HasAnIconAndSaysWhatItDoes()
    {
        // The picker is only usable if a step can be recognised without knowing its id — by its vector icon now,
        // or by the glyph string for a plugin's step that has not set one.
        NodeCatalog.All.Should().OnlyContain(type =>
            (type.IconKind.HasValue || type.Icon.Length > 0) && type.Name.Length > 0 && type.Description.Length > 0);
    }

    [Fact]
    public void EveryStepId_IsUnique()
    {
        NodeCatalog.All.Select(type => type.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Search_FindsAStepByWhatItDoes_NotOnlyByItsName()
    {
        // "Delegate" is called Delegate, but an operator looking for it may well type "background".
        NodeCatalog.Search("shell").Select(type => type.Id).Should().Contain("cockpit.command");
    }

    [Fact]
    public void Search_WithNothingTyped_ShowsEverything()
    {
        NodeCatalog.Search(null).Should().HaveCount(NodeCatalog.All.Count);
        NodeCatalog.Search("   ").Should().HaveCount(NodeCatalog.All.Count);
    }

    [Fact]
    public void ANodeWhoseTypeThisBuildDoesNotHave_DoesNotCrashTheCanvas()
    {
        // A flow saved with a plugin's step, opened on a cockpit without that plugin.
        var node = new WorkflowNode { Id = "x", TypeId = "someplugin.unknown", Name = "Whatever" };

        node.Type.Should().BeNull();
        node.Outputs.Should().Equal(string.Empty);
        node.HasInput.Should().BeTrue();
    }
}
