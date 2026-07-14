using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The switch (#69): one value, a way out per case. Its pins are written into it rather than fixed by its type, and
/// that is where the danger is — a wire remembers its way out by position, so a case inserted in front of the others
/// would hand every wire below it to a different branch, and the error wire (which sits one past the last pin) would
/// quietly become an ordinary one. A flow that silently changed what it does is worse than one that visibly broke.
/// </summary>
public class SwitchTests
{
    [Fact]
    public async Task AValueThatMatchesACase_LeavesByThatCase()
    {
        var outcome = await new SwitchRunner().RunAsync(
            new StepContext(_Switch("{state}", "In Progress, Review, Done"), _Items(("state", "Review")), _Nothing),
            CancellationToken.None);

        outcome.Output.Should().Be("Review");
    }

    // "review" and " Review " are the same status: a flow that fell through over a capital letter is one nobody can
    // debug by looking at it.
    [Fact]
    public async Task Matching_IgnoresCaseAndSurroundingSpace()
    {
        var outcome = await new SwitchRunner().RunAsync(
            new StepContext(_Switch("{state}", " in progress , Review "), _Items(("state", "In Progress")), _Nothing),
            CancellationToken.None);

        outcome.Output.Should().Be("in progress");
    }

    [Fact]
    public async Task AValueThatMatchesNothing_LeavesByOtherwise()
    {
        var outcome = await new SwitchRunner().RunAsync(
            new StepContext(_Switch("{state}", "Review, Done"), _Items(("state", "Backlog")), _Nothing),
            CancellationToken.None);

        outcome.Output.Should().Be(SwitchCases.Otherwise);
    }

    // Not recognising a value is a case you did not name; being handed no value at all is a step configured against
    // data that is not there, and that is a failure like any other unresolved reference.
    [Fact]
    public async Task AValueThatNeverArrived_FailsRatherThanFallingThroughToOtherwise()
    {
        var run = async () => await new SwitchRunner().RunAsync(
            new StepContext(_Switch("{state}", "Review"), _Items(("ticket", "EVE-14")), _Nothing),
            CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*state*");
    }

    [Fact]
    public async Task ASwitchWithNoCases_SaysSoRatherThanSendingEverythingToOtherwise()
    {
        var run = async () => await new SwitchRunner().RunAsync(
            new StepContext(_Switch("{state}", string.Empty), _Items(("state", "Review")), _Nothing),
            CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no cases*");
    }

    [Fact]
    public void ItsWaysOut_AreTheCasesWrittenIntoIt_AndAlwaysEndInOtherwise()
    {
        var node = _Switch("{state}", "In Progress, Review, Done");

        node.Outputs.Should().Equal("In Progress", "Review", "Done", SwitchCases.Otherwise);
    }

    [Fact]
    public void TwoCasesWithTheSameName_BecomeOnePin()
    {
        // Two pins with the same label are two wires the operator cannot tell apart, and only the first could ever
        // be taken.
        var node = _Switch("{state}", "Review, review, Done");

        node.Outputs.Should().Equal("Review", "Done", SwitchCases.Otherwise);
    }

    [Fact]
    public void ACaseAddedInFront_LeavesEveryWireOnTheBranchItWasDrawnTo()
    {
        var (workflow, node, targets) = _Wired("Review, Done");
        var before = node.Outputs.ToList();

        node.Parameters[SwitchCases.CasesParameter] = "In Progress, Review, Done";
        var dropped = workflow.RewireOutputs(node.Id, before);

        dropped.Should().BeEmpty();
        _Target(workflow, node, "Review").Should().Be(targets["Review"]);
        _Target(workflow, node, "Done").Should().Be(targets["Done"]);
        _Target(workflow, node, SwitchCases.Otherwise).Should().Be(targets[SwitchCases.Otherwise]);

        // The error pin sits one past the ordinary ones, so a new case moves it — and the wire from it has to move too.
        workflow.Connections
            .Single(connection => connection.FromNodeId == node.Id && connection.FromOutput == node.ErrorOutput)
            .ToNodeId.Should().Be(targets["error"]);
    }

    [Fact]
    public void ACaseRemoved_TakesTheWireFromItWithIt_AndSaysWhich()
    {
        var (workflow, node, targets) = _Wired("Review, Done");
        var before = node.Outputs.ToList();

        node.Parameters[SwitchCases.CasesParameter] = "Done";
        var dropped = workflow.RewireOutputs(node.Id, before);

        dropped.Should().Equal("Review");
        workflow.Connections.Should().NotContain(connection => connection.ToNodeId == targets["Review"]);
        _Target(workflow, node, "Done").Should().Be(targets["Done"]);
        _Target(workflow, node, SwitchCases.Otherwise).Should().Be(targets[SwitchCases.Otherwise]);
    }

    // A switch with its cases wired up, one wire per pin plus one from the error pin, each to a step of its own.
    private static (Workflow Workflow, WorkflowNode Node, Dictionary<string, string> Targets) _Wired(string cases)
    {
        var workflow = new Workflow { Id = "w", Name = "Flow" };
        var node = _Switch("{state}", cases);
        workflow.Nodes.Add(node);

        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, index) in node.Outputs.Select((label, index) => (label, index)))
        {
            var target = new WorkflowNode { Id = $"n-{label}", TypeId = "cockpit.notify", Name = label };
            workflow.Nodes.Add(target);
            workflow.Connect(node.Id, index, target.Id);
            targets[label] = target.Id;
        }

        var onError = new WorkflowNode { Id = "n-error", TypeId = "cockpit.notify", Name = "Error" };
        workflow.Nodes.Add(onError);
        workflow.Connections.Add(new WorkflowConnection { FromNodeId = node.Id, FromOutput = node.ErrorOutput, ToNodeId = onError.Id });
        targets["error"] = onError.Id;

        return (workflow, node, targets);
    }

    private static string? _Target(Workflow workflow, WorkflowNode node, string label)
    {
        var index = node.Outputs
            .Select((name, position) => (name, position))
            .First(entry => string.Equals(entry.name, label, StringComparison.OrdinalIgnoreCase))
            .position;

        return workflow.Connections
            .SingleOrDefault(connection => connection.FromNodeId == node.Id && connection.FromOutput == index)
            ?.ToNodeId;
    }

    private static WorkflowNode _Switch(string value, string cases)
    {
        var node = new WorkflowNode { Id = "switch", TypeId = SwitchCases.TypeId, Name = "Switch" };
        node.Parameters[SwitchCases.ValueParameter] = value;
        node.Parameters[SwitchCases.CasesParameter] = cases;

        return node;
    }

    private static IReadOnlyList<WorkflowItem> _Items(params (string Field, string Value)[] fields)
    {
        var json = new JsonObject();
        foreach (var (field, value) in fields)
        {
            json[field] = value;
        }

        return [new WorkflowItem(json)];
    }

    private static readonly Dictionary<string, IReadOnlyList<WorkflowItem>> _Nothing = [];
}
