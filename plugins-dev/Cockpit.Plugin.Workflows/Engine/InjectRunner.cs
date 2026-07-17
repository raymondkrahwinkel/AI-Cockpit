using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>Sends text into the session you are working in, as if you had typed it.</summary>
internal sealed class InjectRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.inject";

    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context) =>
        $"Inject into the active session:\n{context.Resolve(context.Node.Parameters.GetValueOrDefault("Text")).Text}";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var text = context.Node.Parameters.GetValueOrDefault("Text");
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("This step has no text to send. Open it and write some.");
        }

        if (!host.Actions.HasActiveSession)
        {
            throw new InvalidOperationException("There is no session to send this to.");
        }

        var resolved = context.Resolve(text);
        await host.Actions.InjectIntoActiveSessionAsync(resolved.Text);

        return StepOutcome.Passing(context.Input, $"Sent to the session: {resolved.Text}");
    }
}
