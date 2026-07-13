using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Stops and asks. For the steps that are not free to undo — a deploy, a ticket moved to Done, a message sent to
/// someone else — where the value of a flow is that it does the work, and the value of this step is that it does not
/// do it behind your back.
/// <para>
/// Saying no is not a failure: nothing went wrong, you said not now. So the run records it as skipped, with what you
/// were asked, and the branch stops there.
/// </para>
/// </summary>
internal sealed class ApproveRunner(ICockpitHost host) : IStepRunner
{
    public string TypeId => "cockpit.approve";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var question = context.Resolve(context.Node.Parameters.GetValueOrDefault("Question")).Text.Trim();
        if (question.Length == 0)
        {
            throw new InvalidOperationException("This step has nothing to ask. Open it and write the question, e.g. \"Move {ticket} to Done?\"");
        }

        var approved = await host.Actions.ConfirmAsync(context.Node.Name, question, "Yes, go on");

        // A refusal ends this branch and says so. Throwing would record it as a failure, and a flow you deliberately
        // stopped is not a flow that broke.
        if (!approved)
        {
            return StepOutcome.Stop("You said not now, so the flow stopped here.");
        }

        return StepOutcome.Passing(context.Input, $"You approved: {question}");
    }
}
