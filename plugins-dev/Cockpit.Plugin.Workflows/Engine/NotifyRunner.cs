using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>Notify: the cockpit's own toast, or Discord when you are away — the same path every other notification takes.</summary>
internal sealed class NotifyRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.notify";

    public Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var message = context.Node.Parameters.GetValueOrDefault("Message");
        if (string.IsNullOrWhiteSpace(message))
        {
            // Nothing to say is not a notification. Failing here is kinder than a blank toast that leaves the
            // operator wondering whether it fired.
            throw new InvalidOperationException("This step has no message to send. Open it and write one.");
        }

        // {output} and {Some step.field} are filled from what the steps before produced.
        var resolved = context.Resolve(message);
        host.ShowToast(resolved.Text, PluginToastSeverity.Information);

        var note = resolved.Missing.Count > 0
            ? $" (nothing called {string.Join(", ", resolved.Missing.Select(field => $"{{{field}}}"))} came in, so it was left as written)"
            : string.Empty;

        return Task.FromResult(StepOutcome.Passing(context.Input, $"Notified: {resolved.Text}{note}"));
    }
}
