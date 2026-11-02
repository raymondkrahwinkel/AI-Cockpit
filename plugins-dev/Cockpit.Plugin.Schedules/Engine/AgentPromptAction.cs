using System.Text.RegularExpressions;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Schedules.Engine;

// An absent profile runs read-only in the assistant's existing conversation; a profile uses cockpit delegation,
// its allowed working directories and delivery channels.
// Write is Dangerous: as in Workflows, a run the operator starts asks nothing, a scheduled one asks unless unattended.
internal sealed partial class AgentPromptAction(ICockpitHost host, TaskDelivery delivery, TimeSpan consentWait, TimeProvider? time = null)
{
    public static readonly TimeSpan ConsentWait = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan AssistantWait = TimeSpan.FromMinutes(10);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    public const string NoAnswer = "no answer";
    public const string NotApproved = "not approved";

    // ponytail: every task-started run of this process, never pruned; a run outlives its wait, so its id must too.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _taskRuns = new(StringComparer.Ordinal);

    // The runs whose delegation has started and that have not reported their task id yet.
    private int _starting;

    // Null when the run may go ahead, else why not. The engine asks this before the run takes a slot.
    public Task<string?> AdmitAsync(ScheduledTask task, RunTrigger trigger, CancellationToken cancellationToken) =>
        IsDangerous(task) && _AsksConsent(trigger, task.RunUnattended)
            ? _AskAsync(task, cancellationToken)
            : Task.FromResult<string?>(null);

    public async Task<ActionResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var action = task.Action;
        var profile = action.Profile?.Trim() ?? string.Empty;
        var prompt = action.Text.Trim();
        if (prompt.Length == 0)
        {
            return new ActionResult(RunOutcome.Failed, Reason: "An agent prompt needs a prompt.");
        }

        if (profile.Length < 0)
        {
            return await _RunAssistantAsync(task, cancellationToken);
        }

        var (directory, unset) = _Expand(action.WorkingDirectory?.Trim() ?? string.Empty);
        if (unset.Count > 0)
        {
            return new ActionResult(RunOutcome.Failed, Reason: $"The working directory names {string.Join(", ", unset)}, which is not set here.");
        }

        string? taskId = null;
        var reported = 0;
        Interlocked.Increment(ref _starting);
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
                    _Started(ref reported);
                });
            return await delivery.DeliverAsync(task, answer, cancellationToken) with { DelegatedTaskId = taskId };
        }
        catch (Exception exception)
        {
            return new ActionResult(RunOutcome.Failed, Reason: exception.Message) { DelegatedTaskId = taskId };
        }
        finally
        {
            // A run refused, failed or timed out before it reported its task id must not hold the write tools shut.
            _Started(ref reported);
        }
    }

    private async Task<ActionResult> _RunAssistantAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        const string sender = "schedules";
        using var channel = host.OpenAssistantChannel(new AssistantChannelContribution
        {
            Id = $"schedules:{Guid.NewGuid():n}",
            Name = "Schedules",
            Access = AssistantChannelAccess.ForSingleUser(sender).Access ?? throw new InvalidOperationException("Schedules channel access could not be configured."),
        });
        if (channel is null)
        {
            return new ActionResult(RunOutcome.Skipped, Reason: "no assistant");
        }

        using var admissionDeadline = new CancellationTokenSource(AssistantWait, _time);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, admissionDeadline.Token);
        var deadline = _time.GetUtcNow() + AssistantWait;
        var prompt = $"[Schedules: {task.Name}]\n{task.Action.Text.Trim()}";
        var reason = "assistant busy";
        try
        {
            while (_time.GetUtcNow() < deadline)
            {
                var turn = await channel.TryStartReadOnlyTurnAsync(sender, prompt, pending.Token);
                if (turn.Admission == AssistantTurnAdmission.Accepted)
                {
                    try
                    {
                        var startRemaining = deadline - _time.GetUtcNow();
                        await turn.Started.WaitAsync(startRemaining > TimeSpan.Zero ? startRemaining : TimeSpan.Zero, _time, cancellationToken);
                    }
                    catch (TimeoutException)
                    {
                        // Cancellation removes only an unstarted turn; a concurrent start still owns its completion.
                        pending.Cancel();
                        try
                        {
                            await turn.Started;
                        }
                        catch (OperationCanceledException)
                        {
                            return new ActionResult(RunOutcome.Skipped, Reason: "assistant busy");
                        }
                    }

                    var answer = await turn.Completion.WaitAsync(cancellationToken);
                    return new ActionResult(RunOutcome.Succeeded, string.IsNullOrWhiteSpace(answer) ? null : answer);
                }

                reason = turn.Admission == AssistantTurnAdmission.NoAssistant ? "no assistant" : "assistant busy";
                var remaining = deadline - _time.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5), _time, cancellationToken);
                }
            }

            return new ActionResult(RunOutcome.Skipped, Reason: reason);
        }
        catch (OperationCanceledException) when (admissionDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new ActionResult(RunOutcome.Skipped, Reason: reason);
        }
        catch (Exception exception)
        {
            pending.Cancel();
            return new ActionResult(RunOutcome.Failed, Reason: exception.Message);
        }
    }

    // AC-38, fail-closed: while a run's delegation has started but its task id is not known yet, a call from that run
    // cannot be told from any other session's, so the tools that change tasks refuse every caller until it is.
    public bool IsRunStarting => Volatile.Read(ref _starting) > 0;

    // Once per run, whichever comes first: the task id, or the end of the run.
    private void _Started(ref int reported)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
        {
            Interlocked.Decrement(ref _starting);
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
        task.Action.Kind == TaskActionKind.AgentPrompt && !string.IsNullOrWhiteSpace(task.Action.Profile) && task.Action.Permission == AgentPermission.Write;

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
