using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// Using what the step before produced (#69). The cockpit's own syntax is one thing — a field name in braces — and
// its one rule is that a field which is not there is never quietly turned into nothing: a command with an empty
// string where a path should be is a worse outcome than a command that visibly did not resolve.
public class StepDataTests
{
    [Fact]
    public void APlaceholder_IsFilledFromWhatTheStepWasHanded()
    {
        var result = StepData.Resolve("Done: {output}", _Items(("output", "3 files changed")));

        Assert.Equal("Done: 3 files changed", result.Text);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void AFieldThatIsNotThere_IsLeftAsWrittenAndReported()
    {
        var result = StepData.Resolve("Branch {branch}", _Items(("output", "x")));

        Assert.Equal("Branch {branch}", result.Text);
        Assert.Equal(new[] { "branch" }, result.Missing);
    }

    [Fact]
    public void TextWithoutPlaceholders_IsHandedBackUntouched()
    {
        var result = StepData.Resolve("git status", _Items(("output", "x")));

        Assert.Equal("git status", result.Text);
        Assert.Empty(result.Missing);
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

        Assert.Equal("Command said: 3 files changed", result.Text);
        Assert.Empty(result.Missing);
    }

    [Fact]
    public void AStepNameNothingCarries_IsMissing_NotQuietlyReadAsAField()
    {
        var result = StepData.Resolve("{Fetch log.path}", _Items(("output", "x")), new Dictionary<string, IReadOnlyList<WorkflowItem>>());

        Assert.Equal("{Fetch log.path}", result.Text);
        Assert.Equal(new[] { "Fetch log.path" }, result.Missing);
    }

    [Fact]
    public void TheFieldsOnOffer_AreThoseOfTheItemTheStepReceives()
    {
        Assert.Equal(new[] { "output", "exitCode" }, StepData.FieldsOf(_Items(("output", "x"), ("exitCode", "0"))));
    }

    [Fact]
    public void WithNothingFlowingIn_ThereIsNothingToOffer()
    {
        Assert.Empty(StepData.FieldsOf([]));
        Assert.Equal(new[] { "output" }, StepData.Resolve("{output}", []).Missing);
    }

    [Fact]
    public void WithAnEscaper_OnlyTheSubstitutedValueIsQuoted_NotTheTemplate()
    {
        var result = StepData.Resolve("echo {output}", _Items(("output", "a; rm -rf ~")), escapeValue: ShellQuoting.QuotePosix);

        Assert.Equal("echo 'a; rm -rf ~'", result.Text);
    }

    [Fact]
    public void WithAnEscaper_AComputedValueIsQuotedToo_NotOnlyAPlainField()
    {
        var result = StepData.Resolve("echo {= 'a; b' }", [], escapeValue: ShellQuoting.QuotePosix);

        Assert.Equal("echo 'a; b'", result.Text);
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
