using System.Text.RegularExpressions;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Schedules.Engine;

// The agent prompt (plan §3, §5): the prompt goes to a profile through the cockpit's own delegation, so the profile's
// rules (its allowed working directories among them) refuse it before a session exists, and the answer is delivered.
// Write is Dangerous: as in Workflows, a run the operator starts asks nothing, a scheduled one asks unless unattended.
internal sealed partial class AgentPromptAction(ICockpitHost host, TaskDelivery delivery, TimeSpan consentWait)
{
    public static readonly TimeSpan ConsentWait = TimeSpan.FromMinutes(60);
    public const string NoAnswer = "no answer";
    public const string NotApproved = "not approved";

    public async Task<ActionResult> RunAsync(ScheduledTask task, RunTrigger trigger, CancellationToken cancellationToken)
    {
        var action = task.Action;
        var profile = action.Profile?.Trim() ?? string.Empty;
        var prompt = action.Text.Trim();
        if (profile.Length == 0 || prompt.Length == 0)
        {
            return new ActionResult(RunOutcome.Failed, Reason: "An agent prompt needs a profile and a prompt.");
        }

        var directory = _Expand(action.WorkingDirectory?.Trim() ?? string.Empty);
        if (action.Permission == AgentPermission.Write && _AsksConsent(trigger, task.RunUnattended))
        {
            if (await _AskAsync(task, profile, directory, prompt, cancellationToken) is { } refused)
            {
                return new ActionResult(RunOutcome.Skipped, Reason: refused);
            }
        }

        string? taskId = null;
        try
        {
            var answer = await host.Actions.DelegateAsync(
                profile,
                prompt,
                directory.Length == 0 ? null : directory,
                action.Timeout,
                action.Permission == AgentPermission.Write ? "acceptEdits" : null,
                id => taskId = id);
            return delivery.Deliver(task, string.Empty) with { DelegatedTaskId = taskId };
        }
        catch (Exception exception)
        {
            return new ActionResult(RunOutcome.Failed, Reason: exception.Message) { DelegatedTaskId = taskId };
        }
    }

    // MCP always asks: an agent's say-so is not the operator's. Only the operator's own "Run now" skips the question.
    private static bool _AsksConsent(RunTrigger trigger, bool unattended) => trigger switch
    {
        RunTrigger.Manual => false,
        RunTrigger.Mcp => true,
        _ => !unattended,
    };

    // Null when approved, else why not. Asked outside the caller's flow, as Workflows' ApproveRunner (AC-1360): an MCP
    // caller's pane would otherwise let that session's "allow all" answer, and a skipped card (Bypassed) is not a yes.
    private async Task<string?> _AskAsync(ScheduledTask task, string profile, string directory, string prompt, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(consentWait);
        var request = new ConsentRequest(
            $"Schedule wants to run: {task.Name}",
            $"Delegate to {profile}, allowed to change files, in {(directory.Length == 0 ? "the profile's own directory" : directory)}:\n{prompt}",
            new ConsentSource(null, null, "Schedules"),
            "schedules.agent-prompt",
            ConsentRisk.Dangerous);

        Task<ConsentDecision> asking;
        using (ExecutionContext.SuppressFlow())
        {
            asking = Task.Run(() => host.RequestConsentAsync(request, deadline.Token), CancellationToken.None);
        }

        var decision = await asking;
        if (decision.IsApproved && !decision.Bypassed)
        {
            return null;
        }

        return deadline.IsCancellationRequested ? NoAnswer : NotApproved;
    }

    private static string _Expand(string path) =>
        Environment.ExpandEnvironmentVariables(Variable().Replace(path, match =>
            Environment.GetEnvironmentVariable(match.Groups[1].Value + match.Groups[2].Value) ?? match.Value));

    [GeneratedRegex(@"\$(?:\{(\w+)\}|(\w+))")]
    private static partial Regex Variable();
}
