using System.Text.Json;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Saving a flow (#69). What a step <em>is</em> — its kind, its ways out, the values a field can take — is looked up
/// from its type id, not stored: storing it would be storing the same thing twice, and the copy would go stale the day
/// the type changed.
/// <para>
/// It also cannot be stored. A type can carry a function (the statuses a board allows, fetched when the field is
/// opened), and a function does not go into JSON: saving a flow threw <c>NotSupportedException</c> and took the app
/// with it — from clicking a template, which is the first thing anybody does.
/// </para>
/// </summary>
public class WorkflowJsonRoundTripTests
{
    [Fact]
    public void AFlowWithAStepThatOffersSuggestions_CanBeSaved()
    {
        // A contributed type with a Suggest function — exactly what a YouTrack status field has.
        NodeCatalog.Contribute([
            new NodeTypeDescriptor(
                "youtrack.status",
                "Set ticket status",
                "Moves a ticket.",
                "↦",
                NodeCategory.External,
                WorkflowNodeKind.Action,
                [""],
                ["Ticket", "Status"],
                Suggest: (_, _) => Task.FromResult<IReadOnlyList<string>>(["In Progress", "Review"])),
        ]);

        var flow = new Workflow { Id = "w", Name = "Ticket → agent" };
        flow.Nodes.Add(new WorkflowNode { Id = "n", TypeId = "youtrack.status", Name = "Move it" });

        var save = () => WorkflowJson.WriteAll([flow]);

        var ex = Record.Exception(save);
        Assert.False(ex is NotSupportedException);

        NodeCatalog.Contribute([]);
    }

    [Fact]
    public void WhatIsSaved_IsTheFlow_NotWhatCanBeLookedUpFromIt()
    {
        var flow = new Workflow { Id = "w", Name = "Flow" };
        var node = new WorkflowNode { Id = "n", TypeId = "cockpit.command", Name = "Cut the branch", X = 80, Y = 160 };
        node.Parameters["Command"] = "git switch -c {branch}";
        flow.Nodes.Add(node);

        var json = JsonDocument.Parse(WorkflowJson.Write(flow)).RootElement;
        var saved = json.GetProperty("Nodes")[0];

        Assert.Equal("cockpit.command", saved.GetProperty("TypeId").GetString());
        Assert.False(saved.TryGetProperty("Type", out _), "the type is looked up from the id, not stored beside it");
        Assert.False(saved.TryGetProperty("Outputs", out _), "what a step's ways out are follows from its type");
        Assert.False(saved.TryGetProperty("Kind", out _), "so does what kind of step it is");
    }

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
