using Cockpit.Plugin.Workflows.Engine;
using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Workflows.Tests;

// The start-session step, and the name it may open a session under (#AC-312). The name is a template like every other
// parameter here, so a flow triggered by a ticket can open its session already called after that ticket instead of
// renaming it a step later.
public class StartSessionRunnerTests
{
    [Fact]
    public async Task TheNameIsPassedOn_ResolvedFromTheStepsInput()
    {
        var (host, actions) = _Host();
        var node = _Start("Claude");
        node.Parameters["Session name"] = "{ticket}";

        await new StartSessionRunner(host).RunAsync(_Context(node, ("ticket", "AC-312")), CancellationToken.None);

        await actions.Received(1).StartSessionAsync(Arg.Is("Claude"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Is("AC-312"));
    }

    // Null, not "": the host reads a blank name as "nobody said", and an empty string would look like a name the
    // caller chose — which is the difference between a session a ticket may still relabel and one it may not.
    [Fact]
    public async Task AStepWithNoName_AsksForNoName()
    {
        var (host, actions) = _Host();

        await new StartSessionRunner(host).RunAsync(_Context(_Start("Claude")), CancellationToken.None);

        await actions.Received(1).StartSessionAsync(Arg.Is("Claude"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Is((string?)null));
    }

    [Fact]
    public async Task ANameThatIsOnlyWhitespace_CountsAsNoName()
    {
        var (host, actions) = _Host();
        var node = _Start("Claude");
        node.Parameters["Session name"] = "   ";

        await new StartSessionRunner(host).RunAsync(_Context(node), CancellationToken.None);

        await actions.Received(1).StartSessionAsync(Arg.Is("Claude"), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Is((string?)null));
    }

    private static (ICockpitHost Host, ICockpitActions Actions) _Host()
    {
        var actions = Substitute.For<ICockpitActions>();
        actions.StartSessionAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.FromResult("the session"));

        var host = Substitute.For<ICockpitHost>();
        host.Actions.Returns(actions);
        return (host, actions);
    }

    private static WorkflowNode _Start(string profile) => new()
    {
        Id = "s",
        TypeId = "cockpit.start-session",
        Name = "Start session",
        Parameters = { ["Profile"] = profile },
    };

    private static StepContext _Context(WorkflowNode node, params (string Field, string Value)[] fields)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        foreach (var (field, value) in fields)
        {
            json[field] = value;
        }

        IReadOnlyList<WorkflowItem> input = fields.Length == 0 ? [] : [new WorkflowItem(json)];
        return new StepContext(node, input, new Dictionary<string, IReadOnlyList<WorkflowItem>>());
    }
}
