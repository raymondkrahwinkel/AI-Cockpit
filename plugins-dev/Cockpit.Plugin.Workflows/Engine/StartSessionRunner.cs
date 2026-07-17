using Cockpit.Plugin.Workflows.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Opens a session on a profile and hands it a prompt — the step that makes this a cockpit's workflow engine rather
/// than a general one. A flow that cuts a branch, moves a ticket and then puts an agent to work on it in the right
/// directory is the whole of Raymond's morning, and this is the step in the middle of it.
/// </summary>
internal sealed class StartSessionRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.start-session";

    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context)
    {
        var profile = context.Resolve(context.Node.Parameters.GetValueOrDefault("Profile")).Text.Trim();
        var prompt = context.Resolve(context.Node.Parameters.GetValueOrDefault("Prompt")).Text.Trim();
        return prompt.Length == 0 ? $"Start a session on {profile}" : $"Start a session on {profile}:\n{prompt}";
    }

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var profile = context.Resolve(context.Node.Parameters.GetValueOrDefault("Profile")).Text.Trim();
        if (profile.Length == 0)
        {
            throw new InvalidOperationException("This step has no profile. Open it and name one — the label you gave it in the cockpit.");
        }

        var prompt = context.Resolve(context.Node.Parameters.GetValueOrDefault("Prompt")).Text;
        var directory = context.Resolve(context.Node.Parameters.GetValueOrDefault("Working directory")).Text.Trim();

        var name = await host.Actions.StartSessionAsync(profile, prompt, directory.Length == 0 ? null : directory);

        return new StepOutcome(
            [
                WorkflowItem.Of(new Dictionary<string, string>
                {
                    ["session"] = name,
                    ["profile"] = profile,
                }),
            ],
            $"Started '{name}' on {profile}.");
    }
}
