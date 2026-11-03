using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Tests;

// The command step, against a real shell (#69). This is the step that makes a flow more than a chain of
// announcements: what a command prints becomes the data the next step gets. And a command that fails is a step
// that fails — an exit code nobody looks at is how a flow ends up reporting green while nothing happened.
public class CommandRunnerTests
{
    [Fact]
    public async Task AnUpstreamValueThatLooksLikeAnInjection_IsRunAsText_NotAsASecondCommand()
    {
        // The classic shell-injection: a prior step's value carries "; echo PWNED". It must reach echo as one
        // argument and be printed, never chain a second command (AC-39). Without the fix this printed "hi\nPWNED".
        var context = _Context(_Command("echo {output}"), _Items(("output", "hi; echo PWNED")));

        var outcome = await new CommandRunner().RunAsync(context, CancellationToken.None);

        Assert.Equal("hi; echo PWNED", outcome.Output);
    }

    [Fact]
    public async Task ABacktickInAnUpstreamValue_IsNotExecuted_AsACommandSubstitution()
    {
        var context = _Context(_Command("echo {output}"), _Items(("output", "a`whoami`b")));

        var outcome = await new CommandRunner().RunAsync(context, CancellationToken.None);

        Assert.Equal("a`whoami`b", outcome.Output);
    }

    private static WorkflowNode _Command(string command) => new()
    {
        Id = "c",
        TypeId = "cockpit.command",
        Name = "Run a command",
        Parameters = { ["Command"] = command },
    };

    private static StepContext _Context(WorkflowNode node, IReadOnlyList<WorkflowItem> input) =>
        new(node, input, new Dictionary<string, IReadOnlyList<WorkflowItem>>());

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
