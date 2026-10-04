using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

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

    // AC-1488: a working directory names a variable of the server's environment, `$NAME` or `${NAME}`; a variable that
    // is not set fails the step naming it, instead of running in an empty path.
    [Fact]
    public async Task AWorkingDirectory_TakesAVariableFromTheEnvironment_AndFailsOnAnUnsetOne()
    {
        Environment.SetEnvironmentVariable("COCKPIT_TEST_ROOT", "/srv/root");
        var host = Substitute.For<ICockpitHost>();
        var runner = new DelegateRunner(host);

        await runner.RunAsync(_Context(_Delegate("$COCKPIT_TEST_ROOT/x"), []), CancellationToken.None);
        await runner.RunAsync(_Context(_Delegate("${COCKPIT_TEST_ROOT}/y"), []), CancellationToken.None);
        var unset = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(_Context(_Delegate("$COCKPIT_TEST_NOPE/x"), []), CancellationToken.None));

        await host.Actions.Received(1).DelegateAsync("Zyra", "hi", "/srv/root/x", null, null);
        await host.Actions.Received(1).DelegateAsync("Zyra", "hi", "/srv/root/y", null, null);
        Assert.Contains("$COCKPIT_TEST_NOPE", unset.Message);
    }

    private static WorkflowNode _Delegate(string directory) => new()
    {
        Id = "d",
        TypeId = "cockpit.delegate",
        Name = "Delegate",
        Parameters = { ["Profile"] = "Zyra", ["Prompt"] = "hi", ["Working directory"] = directory },
    };

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
