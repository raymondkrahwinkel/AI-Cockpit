using System.Text.Json.Nodes;
using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using FluentAssertions;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// The command step, against a real shell (#69). This is the step that makes a flow more than a chain of
/// announcements: what a command prints becomes the data the next step gets. And a command that fails is a step
/// that fails — an exit code nobody looks at is how a flow ends up reporting green while nothing happened.
/// </summary>
public class CommandRunnerTests
{
    [Fact]
    public async Task WhatTheCommandPrints_BecomesTheDataTheNextStepGets()
    {
        var node = _Command("echo hello from the flow");

        var outcome = await new CommandRunner().RunAsync(_Context(node), CancellationToken.None);

        outcome.Output.Should().Be("hello from the flow");
        outcome.Items.Single().Json["output"]!.ToString().Should().Be("hello from the flow");
    }

    [Fact]
    public async Task ACommandThatFails_FailsTheStep_AndSaysWhy()
    {
        var node = _Command("echo it broke >&2; exit 3");

        var run = async () => await new CommandRunner().RunAsync(_Context(node), CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("exited with 3").And.Contain("it broke");
    }

    [Fact]
    public async Task AStepWithNoCommand_SaysSo_RatherThanQuietlyDoingNothing()
    {
        var run = async () => await new CommandRunner().RunAsync(_Context(_Command(string.Empty)), CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no command");
    }

    [Fact]
    public async Task AWorkingDirectoryThatDoesNotExist_IsSaidPlainly_NotSwallowed()
    {
        var node = _Command("pwd");
        node.Parameters["Working directory"] = "/there/is/no/such/place";

        var run = async () => await new CommandRunner().RunAsync(_Context(node), CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no directory");
    }

    [Fact]
    public async Task TheCommandRunsWhereItWasTold()
    {
        var node = _Command("pwd");
        node.Parameters["Working directory"] = Path.GetTempPath().TrimEnd('/');

        var outcome = await new CommandRunner().RunAsync(_Context(node), CancellationToken.None);

        outcome.Output.Should().Contain("tmp");
    }

    [Fact]
    public async Task AnUpstreamValueThatLooksLikeAnInjection_IsRunAsText_NotAsASecondCommand()
    {
        // The classic shell-injection: a prior step's value carries "; echo PWNED". It must reach echo as one
        // argument and be printed, never chain a second command (AC-39). Without the fix this printed "hi\nPWNED".
        var context = _Context(_Command("echo {output}"), _Items(("output", "hi; echo PWNED")));

        var outcome = await new CommandRunner().RunAsync(context, CancellationToken.None);

        outcome.Output.Should().Be("hi; echo PWNED");
    }

    [Fact]
    public async Task ABacktickInAnUpstreamValue_IsNotExecuted_AsACommandSubstitution()
    {
        var context = _Context(_Command("echo {output}"), _Items(("output", "a`whoami`b")));

        var outcome = await new CommandRunner().RunAsync(context, CancellationToken.None);

        outcome.Output.Should().Be("a`whoami`b");
    }

    [Fact]
    public async Task TheOperatorsOwnShellFeatures_InTheTemplate_StillWork()
    {
        // Only substituted values are quoted; the operator's template keeps its shell — the && chains as written.
        var outcome = await new CommandRunner().RunAsync(_Context(_Command("echo one && echo two")), CancellationToken.None);

        outcome.Output.Should().Contain("one").And.Contain("two");
    }

    private static WorkflowNode _Command(string command) => new()
    {
        Id = "c",
        TypeId = "cockpit.command",
        Name = "Run a command",
        Parameters = { ["Command"] = command },
    };

    private static StepContext _Context(WorkflowNode node) => new(node, [], new Dictionary<string, IReadOnlyList<WorkflowItem>>());

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
