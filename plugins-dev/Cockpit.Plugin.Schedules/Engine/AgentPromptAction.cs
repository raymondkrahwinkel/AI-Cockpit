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

    // ponytail: every task-started run of this process, never pruned; a run outlives its wait, so its id must too.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _taskRuns = new(StringComparer.Ordinal);

    // Null when the run may go ahead, else why not. The engine asks this before the run takes a slot.
    public Task<string?> AdmitAsync(ScheduledTask task, RunTrigger trigger, CancellationToken cancellationToken) =>
        task.Action.Permission == AgentPermission.Write && _AsksConsent(trigger, task.RunUnattended)
            ? _AskAsync(task, cancellationToken)
            : Task.FromResult<string?>(null);

    public async Task<ActionResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var action = task.Action;
        var profile = action.Profile?.Trim() ?? string.Empty;
        var prompt = action.Text.Trim();
        if (profile.Length == 0 || prompt.Length == 0)
        {
            return new ActionResult(RunOutcome.Failed, Reason: "An agent prompt needs a profile and a prompt.");
        }

        var (directory, unset) = _Expand(action.WorkingDirectory?.Trim() ?? string.Empty);
        if (unset.Count > 0)
        {
            return new ActionResult(RunOutcome.Failed, Reason: $"The working directory names {string.Join(", ", unset)}, which is not set here.");
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
                id =>
                {
                    taskId = id;
                    _taskRuns[id] = 0;
                });
            return await delivery.DeliverAsync(task, answer, cancellationToken) with { DelegatedTaskId = taskId };
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

    // Dangerous (plan §3): a prompt allowed to change files, the rule the MCP guard holds a task to (AC-38).
    public static bool IsDangerous(ScheduledTask task) =>
        task.Action.Kind == TaskActionKind.AgentPrompt && task.Action.Permission == AgentPermission.Write;

    // Whether the MCP call in hand comes from a run a task started: a delegated task's verified pane id is its task id.
    public bool IsTaskRun(string? paneId) => paneId is not null && _taskRuns.ContainsKey(paneId);

    // The literal thing that runs, as a consent card shows it: profile, directory and prompt.
    public static string Describe(ScheduledTask task)
    {
        var (directory, _) = _Expand(task.Action.WorkingDirectory?.Trim() ?? string.Empty);
        return $"Delegate to {task.Action.Profile}, allowed to change files, in {(directory.Length == 0 ? "the profile's own directory" : directory)}:\n{task.Action.Text.Trim()}";
    }

    private Task<string?> _AskAsync(ScheduledTask task, CancellationToken cancellationToken) => AskAsync(
        host,
        new ConsentRequest($"Schedule wants to run: {task.Name}", Describe(task), new ConsentSource(null, null, "Schedules"), "schedules.agent-prompt", ConsentRisk.Dangerous),
        consentWait,
        cancellationToken);

    // Null when approved, else why not. Asked outside the caller's flow, as Workflows' ApproveRunner (AC-1360): an MCP
    // caller's pane would otherwise let that session's "allow all" answer, and a skipped card (Bypassed) is not a yes.
    public static async Task<string?> AskAsync(ICockpitHost host, ConsentRequest request, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait);

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

    // $VAR, ${VAR} and %VAR% in one pass, so a value is never expanded twice; the names not set come back.
    private static (string Path, List<string> Unset) _Expand(string path)
    {
        var unset = new List<string>();
        var expanded = Variable().Replace(path, match =>
        {
            var name = match.Groups.Values.Skip(1).First(group => group.Success).Value;
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                return value;
            }

            unset.Add(name);
            return match.Value;
        });
        return (expanded, unset);
    }

    [GeneratedRegex(@"\$\{(\w+)\}|\$(\w+)|%(\w+)%")]
    private static partial Regex Variable();
}
