using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Hands the work to another profile and waits for what it produces (#67, #69) — the orchestration node from the
/// braindump. Where "Start session" opens a pane for you to watch, this one is a step in a flow: the flow waits, takes
/// the answer, and carries it to the next step as data.
/// <para>
/// It goes through the cockpit's own delegation, so the task is refused by the same rules an agent's delegation is,
/// and it appears in the delegated-tasks view. A flow does not get a quieter way to run an agent than an agent has.
/// </para>
/// </summary>
internal sealed class DelegateRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.delegate";

    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context)
    {
        var profile = context.Resolve(context.Node.Parameters.GetValueOrDefault("Profile")).Text.Trim();
        var prompt = context.Resolve(context.Node.Parameters.GetValueOrDefault("Prompt")).Text.Trim();
        return $"Delegate to {profile}:\n{prompt}";
    }

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var profile = context.Resolve(context.Node.Parameters.GetValueOrDefault("Profile")).Text.Trim();
        var prompt = context.Resolve(context.Node.Parameters.GetValueOrDefault("Prompt")).Text.Trim();

        if (profile.Length == 0 || prompt.Length == 0)
        {
            throw new InvalidOperationException("This step needs a profile to hand the work to, and the work itself.");
        }

        var directory = context.Resolve(context.Node.Parameters.GetValueOrDefault("Working directory")).Text.Trim();

        var answer = await host.Actions.DelegateAsync(
            profile,
            prompt,
            directory.Length == 0 ? null : directory);

        return new StepOutcome(
            [
                WorkflowItem.Of(new Dictionary<string, string>
                {
                    ["result"] = answer,
                    ["profile"] = profile,
                }),
            ],
            answer.Length == 0 ? $"{profile} answered with nothing." : answer);
    }
}
