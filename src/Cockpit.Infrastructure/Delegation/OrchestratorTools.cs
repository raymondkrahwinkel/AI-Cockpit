using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Delegation;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// The MCP tools a session uses to hand work to another profile (#67), exposed as
/// <c>mcp__cockpit-orchestrator__*</c>. Deliberately thin: every rule about what may be delegated to whom lives
/// in <see cref="IDelegationService"/>, so this class only translates calls and reports refusals honestly — a
/// tool that swallowed a rejection would leave the calling agent guessing why nothing happened.
/// </summary>
/// <remarks>
/// Asynchronous by design: <c>delegate_task</c> returns a task id straight away rather than blocking until the
/// sub-agent is done. A delegated task can run for minutes, which no MCP call should sit through, and the caller
/// keeps the choice between polling progress and just asking for the result.
/// </remarks>
internal sealed class OrchestratorTools
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    private readonly IDelegationService _delegation;

    public OrchestratorTools(IDelegationService delegation)
    {
        _delegation = delegation;
    }

    [McpServerTool(Name = "list_profiles")]
    [Description("Lists the profiles you may delegate a task to, with what each one is meant for and how many tasks it will run at once.")]
    public async Task<string> ListProfilesAsync(CancellationToken cancellationToken)
    {
        var targets = await _delegation.ListTargetsAsync(cancellationToken);
        return JsonSerializer.Serialize(targets, SerializerOptions);
    }

    [McpServerTool(Name = "delegate_task")]
    [Description("Hands a task to another profile, which runs it as a separate session. Returns a task id immediately; the task runs in the background. Returns status 'Queued' when the target profile is already at its concurrency limit.")]
    public async Task<string> DelegateTaskAsync(
        [Description("The label of the profile to delegate to, as returned by list_profiles.")] string profile,
        [Description("The prompt for the delegated session.")] string prompt,
        [Description("The category of work, when the target profile restricts what it accepts (e.g. 'summarize').")] string? task_type,
        [Description("A short human-readable label for this task, shown in the cockpit.")] string? label,
        [Description("The working directory for the task; must be one the target profile allows.")] string? working_directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = await _delegation.DelegateAsync(
                new DelegationRequest(profile, prompt, task_type, label, working_directory),
                cancellationToken);

            return JsonSerializer.Serialize(task, SerializerOptions);
        }
        catch (DelegationRejectedException ex)
        {
            // The refusal is the answer: the agent needs to know it was refused and why, so it can pick another
            // profile or drop the idea, rather than silently believing the work is under way.
            return JsonSerializer.Serialize(new { rejected = true, reason = ex.Message }, SerializerOptions);
        }
    }

    [McpServerTool(Name = "get_task_status")]
    [Description("Reports how a delegated task is doing, without pulling its output.")]
    public string GetTaskStatus(
        [Description("The task id returned by delegate_task.")] string task_id)
    {
        var task = _delegation.GetTask(task_id);
        return task is null
            ? JsonSerializer.Serialize(new { error = $"No task '{task_id}'." }, SerializerOptions)
            : JsonSerializer.Serialize(task, SerializerOptions);
    }

    [McpServerTool(Name = "get_task_result")]
    [Description("Returns a delegated task's answer once it has finished. The point of delegating is to keep that work out of your own context, so this gives you the reply, not the whole transcript — use get_task_output if you need to watch the steps.")]
    public string GetTaskResult(
        [Description("The task id returned by delegate_task.")] string task_id)
    {
        var task = _delegation.GetTask(task_id);
        if (task is null)
        {
            return JsonSerializer.Serialize(new { error = $"No task '{task_id}'." }, SerializerOptions);
        }

        return JsonSerializer.Serialize(
            new { status = task.Status.ToString(), result = task.Result, error = task.Error },
            SerializerOptions);
    }

    [McpServerTool(Name = "get_task_output")]
    [Description("Returns the events a delegated task produced since a cursor, for watching progress. Pass the returned cursor next time to get only what is new.")]
    public string GetTaskOutput(
        [Description("The task id returned by delegate_task.")] string task_id,
        [Description("The cursor from the previous call; omit or pass 0 to start from the beginning.")] int cursor = 0)
    {
        var (events, nextCursor, done) = _delegation.GetOutput(task_id, cursor);

        return JsonSerializer.Serialize(
            new
            {
                events = events.Select(evt => new { type = evt.GetType().Name, text = _Describe(evt) }),
                cursor = nextCursor,
                done,
            },
            SerializerOptions);
    }

    [McpServerTool(Name = "send_followup")]
    [Description("Sends another turn to a delegated task, continuing the same session — including one that has already answered. Poll get_task_status afterwards: the new turn is done when TurnCount has gone up.")]
    public async Task<string> SendFollowUpAsync(
        [Description("The task id returned by delegate_task.")] string task_id,
        [Description("The follow-up message.")] string text,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = await _delegation.SendFollowUpAsync(task_id, text, cancellationToken);
            return JsonSerializer.Serialize(task, SerializerOptions);
        }
        catch (DelegationRejectedException ex)
        {
            // Never a quiet "ok": a caller told the follow-up landed would wait for a turn that is not coming.
            return JsonSerializer.Serialize(new { rejected = true, reason = ex.Message }, SerializerOptions);
        }
    }

    [McpServerTool(Name = "stop_task")]
    [Description("Stops a delegated task and tears its session down.")]
    public async Task<string> StopTaskAsync(
        [Description("The task id returned by delegate_task.")] string task_id)
    {
        var task = await _delegation.StopAsync(task_id);
        return task is null
            ? JsonSerializer.Serialize(new { error = $"No task '{task_id}'." }, SerializerOptions)
            : JsonSerializer.Serialize(task, SerializerOptions);
    }

    [McpServerTool(Name = "list_tasks")]
    [Description("Lists the delegated tasks this cockpit knows about, newest first.")]
    public string ListTasks(
        [Description("Only tasks in this state: Queued, Running, Completed, Failed or Stopped.")] string? status = null)
    {
        DelegatedTaskStatus? filter = Enum.TryParse<DelegatedTaskStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : null;

        return JsonSerializer.Serialize(_delegation.ListTasks(filter), SerializerOptions);
    }

    // Only the events worth reading back to an agent carry text; the rest are reported by type alone, so a
    // progress poll stays small.
    private static string? _Describe(Cockpit.Core.Sessions.SessionEvent evt) => evt switch
    {
        Cockpit.Core.Sessions.AssistantTextCompleted text => text.Text,
        Cockpit.Core.Sessions.ToolUseRequested tool => tool.ToolName,
        Cockpit.Core.Sessions.TurnCompleted turn => turn.Result,
        Cockpit.Core.Sessions.SessionError error => error.Message,
        _ => null,
    };
}
