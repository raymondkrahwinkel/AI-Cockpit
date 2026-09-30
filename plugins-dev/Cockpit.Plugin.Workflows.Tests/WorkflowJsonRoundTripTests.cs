using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// Saving a flow (#69). What a step *is* — its kind, its ways out, the values a field can take — is looked up
// from its type id, not stored: storing it would be storing the same thing twice, and the copy would go stale the day
// the type changed.
//
// It also cannot be stored. A type can carry a function (the statuses a board allows, fetched when the field is
// opened), and a function does not go into JSON: saving a flow threw `NotSupportedException` and took the app
// with it — from clicking a template, which is the first thing anybody does.
public class WorkflowJsonRoundTripTests
{
    [Fact]
    public void AFlow_SurvivesBeingWrittenAndReadBack()
    {
        var flow = new Workflow { Id = "w", Name = "Flow", IsActive = true };
        var node = new WorkflowNode { Id = "n", TypeId = "cockpit.command", Name = "Cut the branch", X = 80, Y = 160, HasErrorPath = true };
        node.Parameters["Command"] = "git switch -c {branch}";
        flow.Nodes.Add(node);
        flow.Nodes.Add(new WorkflowNode { Id = "m", TypeId = "cockpit.notify", Name = "Tell me" });
        flow.Connect("n", 0, "m");

        var read = WorkflowJson.Read(WorkflowJson.Write(flow));

        Assert.NotNull(read);
        Assert.Equal(2, System.Linq.Enumerable.Count(read!.Nodes));
        Assert.Single(read.Connections);
        Assert.Equal("git switch -c {branch}", read.Nodes[0].Parameters["Command"]);
        Assert.True(read.Nodes[0].HasErrorPath, "a step told to show its error pin keeps it across a restart");
    }
}
