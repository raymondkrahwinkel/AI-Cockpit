using System.Text.Json;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

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

        save.Should().NotThrow<NotSupportedException>();

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

        saved.GetProperty("TypeId").GetString().Should().Be("cockpit.command");
        saved.TryGetProperty("Type", out _).Should().BeFalse("the type is looked up from the id, not stored beside it");
        saved.TryGetProperty("Outputs", out _).Should().BeFalse("what a step's ways out are follows from its type");
        saved.TryGetProperty("Kind", out _).Should().BeFalse("so does what kind of step it is");
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

        read.Should().NotBeNull();
        read!.Nodes.Should().HaveCount(2);
        read.Connections.Should().ContainSingle();
        read.Nodes[0].Parameters["Command"].Should().Be("git switch -c {branch}");
        read.Nodes[0].HasErrorPath.Should().BeTrue("a step told to show its error pin keeps it across a restart");
    }
}
