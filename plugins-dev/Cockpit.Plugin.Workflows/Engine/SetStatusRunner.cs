using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Sets the active session's statusline — what it is working on — and optionally renames it (#AC-13). The workflow
/// half of the agent-statusline feature: a flow that starts a session on a ticket then labels it with the ticket
/// number, or a ticket-picked trigger that writes the ticket into the session's status and clears it when done.
/// Acts on the active (selected) session, which is the one a preceding start-session step just opened.
/// </summary>
internal sealed class SetStatusRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.set-status";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var status = context.Resolve(context.Node.Parameters.GetValueOrDefault("Status")).Text;
        var name = context.Resolve(context.Node.Parameters.GetValueOrDefault("Name")).Text.Trim();

        await host.Actions.SetActiveSessionStatusAsync(status, name.Length == 0 ? null : name);

        var renamed = name.Length == 0 ? string.Empty : $", renamed to '{name}'";
        return new StepOutcome(
            context.Input,
            status.Length == 0 ? $"Cleared the session status{renamed}." : $"Set the session status to '{status}'{renamed}.");
    }
}
