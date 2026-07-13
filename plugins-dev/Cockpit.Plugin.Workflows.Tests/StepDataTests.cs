using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// Using what the step before produced (#69). The cockpit's own syntax is one thing — a field name in braces — and
/// its one rule is that a field which is not there is never quietly turned into nothing: a command with an empty
/// string where a path should be is a worse outcome than a command that visibly did not resolve.
/// </summary>
public class StepDataTests
{
    [Fact]
    public void APlaceholder_IsFilledFromWhatTheStepWasHanded()
    {
        var result = StepData.Resolve("Done: {output}", _Items(("output", "3 files changed")));

        result.Text.Should().Be("Done: 3 files changed");
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void AFieldThatIsNotThere_IsLeftAsWrittenAndReported()
    {
        var result = StepData.Resolve("Branch {branch}", _Items(("output", "x")));

        result.Text.Should().Be("Branch {branch}");
        result.Missing.Should().Equal("branch");
    }

    [Fact]
    public void TextWithoutPlaceholders_IsHandedBackUntouched()
    {
        var result = StepData.Resolve("git status", _Items(("output", "x")));

        result.Text.Should().Be("git status");
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void AnEarlierStep_IsReachedByName_NotOnlyTheOneImmediatelyBefore()
    {
        // The difference between a chain and a flow: the notification at the end can quote the command from the
        // middle, without every step in between having to carry it along.
        var produced = new Dictionary<string, IReadOnlyList<WorkflowItem>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Run a command"] = _Items(("output", "3 files changed")),
        };

        var result = StepData.Resolve("Command said: {Run a command.output}", _Items(("output", "something else")), produced);

        result.Text.Should().Be("Command said: 3 files changed");
        result.Missing.Should().BeEmpty();
    }

    [Fact]
    public void AStepNameNothingCarries_IsMissing_NotQuietlyReadAsAField()
    {
        var result = StepData.Resolve("{Fetch log.path}", _Items(("output", "x")), new Dictionary<string, IReadOnlyList<WorkflowItem>>());

        result.Text.Should().Be("{Fetch log.path}");
        result.Missing.Should().Equal("Fetch log.path");
    }

    [Fact]
    public void TheFieldsOnOffer_AreThoseOfTheItemTheStepReceives()
    {
        StepData.FieldsOf(_Items(("output", "x"), ("exitCode", "0")))
            .Should().Equal("output", "exitCode");
    }

    [Fact]
    public void WithNothingFlowingIn_ThereIsNothingToOffer()
    {
        StepData.FieldsOf([]).Should().BeEmpty();
        StepData.Resolve("{output}", []).Missing.Should().Equal("output");
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
}
