using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The steps the cockpit offers (#69) and how the picker finds them. Deliberately cockpit-shaped: the value is in
/// what only this app can do — start sessions, delegate work, watch what an agent says. A general automation kit
/// already exists, and the operator runs it elsewhere.
/// </summary>
public class NodeCatalogTests
{
    [Fact]
    public void ADecision_HasTwoWaysOut_AndTheyAreNamed()
    {
        var decision = NodeCatalog.Find("cockpit.if");

        Assert.NotNull(decision);
        Assert.Equal(new[] { "true", "false" }, decision!.Outputs);
        Assert.Equal(WorkflowNodeKind.Decision, decision.Kind);
    }

    [Fact]
    public void ATrigger_TakesNothingIn()
    {
        Assert.All(NodeCatalog.All.Where(type => type.Kind == WorkflowNodeKind.Trigger), type => Assert.False(type.HasInput));
    }

    [Fact]
    public void EveryStep_HasAnIconAndSaysWhatItDoes()
    {
        // The picker is only usable if a step can be recognised without knowing its id — by its vector icon now,
        // or by the glyph string for a plugin's step that has not set one.
        Assert.All(NodeCatalog.All, type =>
            Assert.True((type.IconKind.HasValue || type.Icon.Length > 0) && type.Name.Length > 0 && type.Description.Length > 0));
    }

    [Fact]
    public void EveryStepId_IsUnique()
    {
        var ids = NodeCatalog.All.Select(type => type.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Search_FindsAStepByWhatItDoes_NotOnlyByItsName()
    {
        // "Delegate" is called Delegate, but an operator looking for it may well type "background".
        Assert.Contains("cockpit.command", NodeCatalog.Search("shell").Select(type => type.Id));
    }

    [Fact]
    public void Search_WithNothingTyped_ShowsEverything()
    {
        Assert.Equal(NodeCatalog.All.Count, System.Linq.Enumerable.Count(NodeCatalog.Search(null)));
        Assert.Equal(NodeCatalog.All.Count, System.Linq.Enumerable.Count(NodeCatalog.Search("   ")));
    }

    [Fact]
    public void ANodeWhoseTypeThisBuildDoesNotHave_DoesNotCrashTheCanvas()
    {
        // A flow saved with a plugin's step, opened on a cockpit without that plugin.
        var node = new WorkflowNode { Id = "x", TypeId = "someplugin.unknown", Name = "Whatever" };

        Assert.Null(node.Type);
        Assert.Equal(new[] { string.Empty }, node.Outputs);
        Assert.True(node.HasInput);
    }
}
