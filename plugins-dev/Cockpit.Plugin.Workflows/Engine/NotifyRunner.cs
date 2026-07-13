using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>Notify: the cockpit's own toast, or Discord when you are away — the same path every other notification takes.</summary>
internal sealed class NotifyRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.notify";

    public Task<StepOutcome> RunAsync(WorkflowNode node, IReadOnlyList<WorkflowItem> input, CancellationToken cancellationToken)
    {
        var message = node.Parameters.GetValueOrDefault("Message");
        if (string.IsNullOrWhiteSpace(message))
        {
            // Nothing to say is not a notification. Failing here is kinder than a blank toast that leaves the
            // operator wondering whether it fired.
            throw new InvalidOperationException("This step has no message to send. Open it and write one.");
        }

        host.ShowToast(message, PluginToastSeverity.Information);
        return Task.FromResult(StepOutcome.Passing(input, $"Notified: {message}"));
    }
}
