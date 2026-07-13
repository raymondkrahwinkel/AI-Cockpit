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

        var outcome = await new CommandRunner().RunAsync(node, [], CancellationToken.None);

        outcome.Output.Should().Be("hello from the flow");
        outcome.Items.Single().Json["output"]!.ToString().Should().Be("hello from the flow");
    }

    [Fact]
    public async Task ACommandThatFails_FailsTheStep_AndSaysWhy()
    {
        var node = _Command("echo it broke >&2; exit 3");

        var run = async () => await new CommandRunner().RunAsync(node, [], CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("exited with 3").And.Contain("it broke");
    }

    [Fact]
    public async Task AStepWithNoCommand_SaysSo_RatherThanQuietlyDoingNothing()
    {
        var run = async () => await new CommandRunner().RunAsync(_Command(string.Empty), [], CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no command");
    }

    [Fact]
    public async Task AWorkingDirectoryThatDoesNotExist_IsSaidPlainly_NotSwallowed()
    {
        var node = _Command("pwd");
        node.Parameters["Working directory"] = "/there/is/no/such/place";

        var run = async () => await new CommandRunner().RunAsync(node, [], CancellationToken.None);

        (await run.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("no directory");
    }

    [Fact]
    public async Task TheCommandRunsWhereItWasTold()
    {
        var node = _Command("pwd");
        node.Parameters["Working directory"] = Path.GetTempPath().TrimEnd('/');

        var outcome = await new CommandRunner().RunAsync(node, [], CancellationToken.None);

        outcome.Output.Should().Contain("tmp");
    }

    private static WorkflowNode _Command(string command) => new()
    {
        Id = "c",
        TypeId = "cockpit.command",
        Name = "Run a command",
        Parameters = { ["Command"] = command },
    };
}
