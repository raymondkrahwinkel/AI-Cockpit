using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>Sends text into the session you are working in, as if you had typed it.</summary>
internal sealed class InjectRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.inject";

    public async Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken)
    {
        var text = node.Parameters.GetValueOrDefault("Text");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("This step has no text to send. Open it and write some.");
        }

        if (!host.Actions.HasActiveSession)
        {
            throw new InvalidOperationException("There is no session to send this to.");
        }

        var resolved = StepData.Resolve(text, input);
        await host.Actions.InjectIntoActiveSessionAsync(resolved.Text);

        return StepOutcome.Passing(input, $"Sent to the session: {resolved.Text}");
    }
}
