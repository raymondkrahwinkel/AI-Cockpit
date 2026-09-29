using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

// Sets the statusline — what a session is working on — of the session the step names, never "the active one"
// (AC-1399), and optionally renames it (#AC-13): a flow that starts a session on a ticket then labels it with the
// ticket number, naming it {Start session.session}.
internal sealed class SetStatusRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.set-status";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var session = SessionTarget.Resolve(host, context);
        var status = context.Resolve(context.Node.Parameters.GetValueOrDefault("Status")).Text;
        var name = context.Resolve(context.Node.Parameters.GetValueOrDefault("Name")).Text.Trim();

        if (!await host.SetSessionStatuslineAsync(session.PaneId, status))
        {
            throw new InvalidOperationException($"'{session.Name}' took no status: it closed before it could be labelled.");
        }

        if (name.Length > 0)
        {
            // A flow naming the session is a name somebody chose, same as a rename — so a ticket linked to that
            // session later offers its name rather than taking it (#AC-310).
            if (!await host.SetSessionNameAsync(session.PaneId, name))
            {
                throw new InvalidOperationException($"'{session.Name}' took the status but not the name '{name}': it closed in between.");
            }
        }

        var renamed = name.Length == 0 ? string.Empty : $", renamed to '{name}'";
        return new StepOutcome(
            context.Input,
            status.Length == 0 ? $"Cleared the status of '{session.Name}'{renamed}." : $"Set the status of '{session.Name}' to '{status}'{renamed}.");
    }
}
