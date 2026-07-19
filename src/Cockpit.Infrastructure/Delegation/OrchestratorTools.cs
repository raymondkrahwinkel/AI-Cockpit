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

    [McpServerTool(Name = "describe_profile")]
    [Description("Records what a profile turned out to be good for, so the next session starts where this one left off: its purpose, its capability tags, and the kinds of work it accepts. Use it after working with a profile — if a model reviewed a frontend diff well but lost the thread on architecture, say so here. Only these three descriptive fields can be set, and only on a profile that is already a delegation target: what a delegated session may actually do (its permission ceiling, the directories it may work in, how many tasks at once) is the operator's to decide, not yours. A field left out is left as it was.")]
    public async Task<string> DescribeProfileAsync(
        [Description("The label of the profile to describe, as returned by list_profiles.")] string profile,
        [Description("What this profile is good for, in a sentence — read by whoever picks a profile next. Omit to leave it as it is.")] string? purpose,
        [Description("Capability tags (code, summarize, cheap, local, …). Omit to leave them as they are; an empty list clears them.")] string[]? tags,
        [Description("The kinds of work this profile should accept (review, refactor, …). Omit to leave them as they are; an empty list accepts anything.")] string[]? task_types,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await _delegation.DescribeTargetAsync(profile, purpose, tags, task_types, cancellationToken);
            return JsonSerializer.Serialize(target, SerializerOptions);
        }
        catch (DelegationRejectedException rejected)
        {
            return JsonSerializer.Serialize(new { error = rejected.Message }, SerializerOptions);
        }
    }

    [McpServerTool(Name = "list_providers")]
    [Description("Lists the providers a session can run under: the local ones you can set up yourself with add_profile (Ollama, LM Studio) and every provider your installed plugins register. Each says whether it is addable with add_profile — the plugin ones are the operator's to create, since a plugin provider may carry a login. Use it before add_profile to pick a valid provider name and to see what is available instead of guessing.")]
    public string ListProviders()
    {
        return JsonSerializer.Serialize(_delegation.ListProviders(), SerializerOptions);
    }

    [McpServerTool(Name = "add_profile")]
    [Description("Adds a LOCAL-model profile (Ollama or LM Studio) so it is ready to use — to start a session under, or for the operator to enrol as a delegation target. Use it when you need a local model to work with and one is not set up yet, instead of editing the profiles file by hand. It is added but NOT enabled as a delegation target: what a delegated session may do (its permission ceiling, its directories, how many at once) is the operator's to set, so you cannot delegate to it until they turn it on in the cockpit's profile settings. Only local models can be added this way; Claude and other logged-in profiles are the operator's to create. The purpose and tags you give are kept as suggestions for when they enable it.")]
    public async Task<string> AddProfileAsync(
        [Description("A unique display label for the new profile.")] string label,
        [Description("The local provider: 'ollama' or 'lmstudio'.")] string provider,
        [Description("The model id as the server reports it (from /v1/models), e.g. 'qwen2.5-coder:7b'.")] string model,
        [Description("The server base URL. Omit for the provider default (Ollama http://localhost:11434, LM Studio http://localhost:1234).")] string? base_url,
        [Description("What this profile is good for, in a sentence — shown in the picker and to whoever enables it.")] string? purpose,
        [Description("Capability tags (code, summarize, cheap, local, …) to help pick it once enabled.")] string[]? tags,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await _delegation.AddLocalModelProfileAsync(label, provider, model, base_url, purpose, tags, cancellationToken);
            return JsonSerializer.Serialize(
                new
                {
                    created,
                    note = "Added and usable to start a session under. It is not a delegation target yet — enable it and " +
                           "set its limits in the cockpit's profile settings before delegating to it.",
                },
                SerializerOptions);
        }
        catch (DelegationRejectedException rejected)
        {
            // The refusal is the answer: a duplicate label, a non-local provider, or a missing model — say which, so
            // the caller can fix the call rather than guess why nothing was added.
            return JsonSerializer.Serialize(new { error = rejected.Message }, SerializerOptions);
        }
    }

    [McpServerTool(Name = "delegate_task")]
    [Description("Hands a task to another profile, which runs it as a separate session. Returns a task id immediately; the task then runs in the background. A status of 'Queued' means the task is accepted and waiting for a free slot on that profile — it will start by itself, so poll get_task_status rather than delegating the same work again.")]
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

            // A queued task is accepted, not rejected — but a bare "Queued" reads to a model like a failure, and
            // it re-sends the same work, piling up tasks the profile will run one after another anyway. So say
            // plainly that it is in hand and that polling, not re-delegating, is what to do next.
            if (task.Status == DelegatedTaskStatus.Queued)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        task,
                        queued = true,
                        note = $"Accepted and queued: '{task.ProfileLabel}' is already running as many tasks as it allows at once. " +
                               "It starts on its own as soon as a slot frees. Do not call delegate_task again for this work — " +
                               "poll get_task_status with this TaskId, and collect the answer with get_task_result.",
                    },
                    SerializerOptions);
            }

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
    // progress poll stays small. A tool result carries its content — above all a gate denial or tool error, which
    // is otherwise invisible (AC-100/AC-113): without it a caller sees a tool ran and a result came back, but not
    // why write_file was refused. An error result is marked so a poll can tell a failure from a normal return.
    private static string? _Describe(Cockpit.Core.Sessions.SessionEvent evt) => evt switch
    {
        Cockpit.Core.Sessions.AssistantTextCompleted text => text.Text,
        Cockpit.Core.Sessions.ToolUseRequested tool => tool.ToolName,
        Cockpit.Core.Sessions.ToolResult result => result.IsError ? $"[error] {result.Content}" : result.Content,
        Cockpit.Core.Sessions.TurnCompleted turn => turn.Result,
        Cockpit.Core.Sessions.SessionError error => error.Message,
        _ => null,
    };
}
