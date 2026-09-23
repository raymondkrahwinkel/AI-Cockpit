using System.Globalization;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Engine;

// Stops and asks. For the steps that are not free to undo — a deploy, a ticket moved to Done, a message sent to
// someone else — where the value of a flow is that it does the work, and the value of this step is that it does not
// do it behind your back.
//
// Saying no is not a failure: nothing went wrong, you said not now. So the run records it as skipped, with what you
// were asked, and the branch stops there.
//
// Asked through the consent broker (AC-1360), so the question reaches wherever a prompt can be answered — the
// banner, or a chat channel's buttons — and a question nobody answers in time is a no.
internal sealed class ApproveRunner(ICockpitHost host) : IStepRunner
{
    public const string TimeoutParameter = "Wait (minutes)";

    // As long as AI-Hub's scheduler waited for an approval: long enough to be away from the desk for a while.
    public const double DefaultTimeoutMinutes = 60;

    // A week: well inside what CancelAfter accepts, and longer than any question worth pausing a flow for.
    private const double _MaxTimeoutMinutes = 7 * 24 * 60;

    public string TypeId => "cockpit.approve";

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var question = context.Resolve(context.Node.Parameters.GetValueOrDefault("Question")).Text.Trim();
        if (question.Length == 0)
        {
            throw new InvalidOperationException("This step has nothing to ask. Open it and write the question, e.g. \"Move {ticket} to Done?\"");
        }

        var minutes = _TimeoutMinutes(context.Resolve(context.Node.Parameters.GetValueOrDefault(TimeoutParameter)).Text);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(minutes));

        var request = new ConsentRequest(
            context.Node.Name,
            question,
            new ConsentSource(null, null, "Workflows"),
            "workflow.cockpit.approve",
            ConsentRisk.Dangerous);

        // Asked outside the caller's flow: an agent that started this run over MCP carries its verified pane id in it,
        // and the assistant's "allow all" would then answer the operator's own question without anyone seeing it.
        Task<ConsentDecision> asking;
        using (ExecutionContext.SuppressFlow())
        {
            asking = Task.Run(() => host.RequestConsentAsync(request, deadline.Token), CancellationToken.None);
        }

        var decision = await asking;

        cancellationToken.ThrowIfCancellationRequested();

        // A refusal ends this branch and says so. Throwing would record it as a failure, and a flow you deliberately
        // stopped is not a flow that broke. Only an answer counts: a skipped card (Bypassed) is not a yes.
        if (!decision.IsApproved || decision.Bypassed)
        {
            return StepOutcome.Stop(deadline.IsCancellationRequested
                ? $"Nobody answered within {minutes.ToString(CultureInfo.InvariantCulture)} minutes, so this counts as not now and the flow stopped here."
                : "Not approved, so the flow stopped here.");
        }

        return StepOutcome.Passing(context.Input, $"You approved: {question}");
    }

    private static double _TimeoutMinutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultTimeoutMinutes;
        }

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
            || !double.IsFinite(minutes)
            || minutes <= 0
            || minutes > _MaxTimeoutMinutes)
        {
            throw new InvalidOperationException($"\"{text.Trim()}\" is not a number of minutes to wait. Write e.g. 60 (at most a week), or leave it blank for {DefaultTimeoutMinutes.ToString(CultureInfo.InvariantCulture)}.");
        }

        return minutes;
    }
}
