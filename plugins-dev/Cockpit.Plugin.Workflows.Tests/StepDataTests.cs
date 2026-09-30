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
