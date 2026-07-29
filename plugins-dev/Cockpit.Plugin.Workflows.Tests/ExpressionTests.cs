using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The computed half of a parameter, and the decision that rests on it (#69). The rule that matters is not what an
/// expression can do — it is what happens when it cannot: a condition nobody can read must stop the step, never
/// quietly count as false and send the flow down the other branch, where nothing in the run says anything went wrong.
/// </summary>
public class ExpressionTests
{
    [Fact]
    public void AnExpression_ComputesOverTheDataOfTheRun()
    {
        var result = StepData.Resolve(@"{= output.split('\n').length } lines", _Items(("output", "a\nb\nc")), _Nothing);

        Assert.Equal("3 lines", result.Text);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void AnExpression_ReachesAnEarlierStepByName()
    {
        var produced = new Dictionary<string, IReadOnlyList<WorkflowItem>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Run a command"] = _Items(("output", "error: nope")),
        };

        var result = StepData.Resolve("{= step('Run a command').output.includes('error') }", _Items(("output", "")), produced);

        Assert.Equal("true", result.Text);
    }

    [Fact]
    public void AnExpressionThatCannotRun_LeavesTheTextAsWrittenAndIsReported()
    {
        var result = StepData.Resolve("{= nope.nope() }", _Items(("output", "x")), _Nothing);

        Assert.Equal("{= nope.nope() }", result.Text);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task ADecision_TakesTheBranchItsConditionSays()
    {
        var outcome = await new IfRunner().RunAsync(
            new StepContext(_If("exitCode != '0'"), _Items(("exitCode", "1")), _Nothing),
            CancellationToken.None);

        Assert.Equal("true", outcome.Output);
    }

    [Fact]
    public async Task ADecision_ReadsItsConditionWithOrWithoutTheBraces()
    {
        var outcome = await new IfRunner().RunAsync(
            new StepContext(_If("{= exitCode == '0' }"), _Items(("exitCode", "0")), _Nothing),
            CancellationToken.None);

        Assert.Equal("true", outcome.Output);
    }

    [Fact]
    public async Task AConditionThatCannotBeRead_FailsTheStep_RatherThanQuietlyCountingAsFalse()
    {
        var run = async () => await new IfRunner().RunAsync(
            new StepContext(_If("this is not javascript"), _Items(("output", "x")), _Nothing),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(run);
    }

    [Fact]
    public async Task ADecisionWithoutACondition_FailsAndSaysWhatToWrite()
    {
        var run = async () => await new IfRunner().RunAsync(
            new StepContext(_If(string.Empty), [], _Nothing),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(run);
        Assert.Contains("no condition", ex.Message);
    }

    private static readonly Dictionary<string, IReadOnlyList<WorkflowItem>> _Nothing = new(StringComparer.OrdinalIgnoreCase);

    private static WorkflowNode _If(string condition)
    {
        var node = new WorkflowNode { Id = "if", TypeId = "cockpit.if", Name = "If" };
        node.Parameters["Condition"] = condition;

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
}
