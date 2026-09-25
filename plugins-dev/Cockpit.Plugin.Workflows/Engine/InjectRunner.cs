using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

// Puts text into the input of the session the step names, as if you had typed it — placed, not sent (AC-1399).
internal sealed class InjectRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.inject";

    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context) =>
        $"Inject into the session '{SessionTarget.Describe(host, context)}':\n{context.Resolve(context.Node.Parameters.GetValueOrDefault("Text")).Text}";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var text = context.Node.Parameters.GetValueOrDefault("Text");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("This step has no text to send. Open it and write some.");
        }

        var session = SessionTarget.Resolve(host, context);
        var resolved = context.Resolve(text);
        if (!await host.InsertIntoSessionAsync(session.PaneId, resolved.Text))
        {
            throw new InvalidOperationException($"'{session.Name}' took no text: it closed, or it has no input to type into.");
        }

        return StepOutcome.Passing(context.Input, $"Put into '{session.Name}': {resolved.Text}");
    }
}
