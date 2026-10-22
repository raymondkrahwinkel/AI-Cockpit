using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugin.Schedules;

// The MCP server cockpit-schedules (AC-1545, plan §6), in every Cockpit session unless a profile's MCP selection leaves
// it out. Follows WorkflowMcpTools, with two deliberate differences: a Dangerous task may be created, but arrives
// switched off; and a run a task started may only read and create reminders (AC-38).
internal sealed class ScheduleMcpTools(
    ICockpitHost host, TaskStore tasks, RunStore runs, ScheduleEngine engine, AgentPromptAction agent, TaskDelivery delivery)
{
    public const string EndpointName = "cockpit-schedules";
    public const string NeedsApproval = "Needs your approval to enable: this task may change files. Ask the operator to switch it on, or call set_enabled, which asks them.";
    public const string NotAvailableYet = "Reminders are not available yet in this build of Schedules.";
    public const string TaskRunsMayNot = "A run that a scheduled task started may only read, and create reminders.";

    private const string TimeRules =
        " Times are ISO-8601 in an IANA zone, never plain words: 'morning' is 08:00, 'afternoon' 13:00, 'evening' 19:00 unless the operator says otherwise. Read the time back to the operator in their own words.";

    private static readonly JsonSerializerOptions Json = new(StoreJson.Options) { WriteIndented = false };

    // One read-modify-write at a time: two MCP calls must not each save a list without the other's change.
    private readonly Lock _gate = new();

    // ── tasks ──────────────────────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_scheduled_tasks", ReadOnly = true)]
    [Description("Lists the scheduled tasks: id, name, schedule and time zone, whether it is on, what it does (a fixed notification or an agent prompt) and where its result goes.")]
    public string ListScheduledTasks() =>
        tasks.Load() is { } all ? _Serialize(all.Select(_Summary)) : _Fail("The stored task list cannot be read.");

    [McpServerTool(Name = "get_scheduled_task", ReadOnly = true)]
    [Description("Returns one scheduled task in full, by its id from list_scheduled_tasks.")]
    public string GetScheduledTask([Description("The task id.")] string id) =>
        _Find(id) is { } task ? _Serialize(task) : _Fail($"No scheduled task with id '{id}'.");

    [McpServerTool(Name = "create_scheduled_task", ReadOnly = false, Destructive = false)]
    [Description("Creates a scheduled task. A task that hands a prompt to an agent allowed to change files (permission 'write') is created switched off until the operator approves it; anything else is on at once." + TimeRules)]
    public string CreateScheduledTask(
        [Description("A short name, e.g. 'Morning news'.")] string name,
        [Description("A 5-field cron in the task's zone ('30 7 * * 1-5' is weekdays at 07:30), 'once 2026-10-23T08:00' (ISO-8601 local time), or 'every 15 m'.")] string schedule,
        [Description("An IANA time zone, e.g. 'Europe/Amsterdam'. Required: a time without a zone is the gap this asks you to close.")] string timeZone,
        [Description("'notification' delivers the text as it is; 'agent_prompt' hands the text to a profile as its prompt and delivers the answer.")] string action,
        [Description("The notification text, or the agent's prompt.")] string text,
        [Description("For agent_prompt: the profile to hand it to.")] string? profile = null,
        [Description("For agent_prompt: the working directory, which the profile must allow; $VAR is expanded.")] string? workingDirectory = null,
        [Description("For agent_prompt: 'read_only' (default) or 'write' (may change files, so it needs the operator's approval).")] string permission = "read_only",
        [Description("For agent_prompt: minutes to wait for the answer; leave out for the host's default.")] int? timeoutMinutes = null,
        [Description("Where the result goes: 'cockpit', or a channel from list_delivery_channels as 'discord' or 'discord:dm'. Leave out for the cockpit notification.")] string[]? delivery = null)
    {
        if (_RefuseTaskRun() is { } refused)
        {
            return refused;
        }

        var built = _Build(new ScheduledTask
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = name,
            Schedule = schedule,
            TimeZone = timeZone,
            Action = new TaskAction(TaskActionKind.Notification, text),
        }, schedule, timeZone, action, text, profile, workingDirectory, permission,
            timeoutMinutes is > 0 ? TimeSpan.FromMinutes(timeoutMinutes.Value) : null,
            delivery, [new DeliveryTarget(DeliveryTarget.CockpitNotification)]);
        if (built.Error is { } error)
        {
            return _Fail(error);
        }

        return _Save(built.Task, replacing: null);
    }

    [McpServerTool(Name = "update_scheduled_task", ReadOnly = false, Destructive = true)]
    [Description("Changes a scheduled task; what you leave out stays as it is. A task that ends up handing a prompt to an agent allowed to change files is switched off until the operator approves it again." + TimeRules)]
    public string UpdateScheduledTask(
        [Description("The task id.")] string id,
        [Description("A new name.")] string? name = null,
        [Description("A new schedule, in the same forms as create_scheduled_task.")] string? schedule = null,
        [Description("A new IANA time zone.")] string? timeZone = null,
        [Description("'notification' or 'agent_prompt'.")] string? action = null,
        [Description("The new text or prompt.")] string? text = null,
        [Description("For agent_prompt: the profile.")] string? profile = null,
        [Description("For agent_prompt: the working directory.")] string? workingDirectory = null,
        [Description("For agent_prompt: 'read_only' or 'write'.")] string? permission = null,
        [Description("For agent_prompt: minutes to wait for the answer.")] int? timeoutMinutes = null,
        [Description("Where the result goes, replacing the list, as in create_scheduled_task.")] string[]? delivery = null)
    {
        if (_RefuseTaskRun() is { } refused)
        {
            return refused;
        }

        if (_Find(id) is not { } current)
        {
            return _Fail($"No scheduled task with id '{id}'.");
        }

        var built = _Build(
            current with { Name = name ?? current.Name },
            schedule ?? current.Schedule,
            timeZone ?? current.TimeZone,
            action ?? (current.Action.Kind == TaskActionKind.AgentPrompt ? "agent_prompt" : "notification"),
            text ?? current.Action.Text,
            profile ?? current.Action.Profile,
            workingDirectory ?? current.Action.WorkingDirectory,
            permission ?? (current.Action.Permission == AgentPermission.Write ? "write" : "read_only"),
            timeoutMinutes is > 0 ? TimeSpan.FromMinutes(timeoutMinutes.Value) : current.Action.Timeout,
            delivery,
            current.Delivery);
        return built.Error is { } error ? _Fail(error) : _Save(built.Task, replacing: id);
    }

    [McpServerTool(Name = "delete_scheduled_task", ReadOnly = false, Destructive = true)]
    [Description("Deletes a scheduled task. Its run history stays until it ages out.")]
    public string DeleteScheduledTask([Description("The task id.")] string id)
    {
        if (_RefuseTaskRun() is { } refused)
        {
            return refused;
        }

        lock (_gate)
        {
            if (tasks.Load() is not { } all)
            {
                return _Fail("The stored task list cannot be read.");
            }

            if (all.All(task => task.Id != id))
            {
                return _Fail($"No scheduled task with id '{id}'.");
            }

            tasks.Save([.. all.Where(task => task.Id != id)]);
        }

        return _Serialize(new { ok = true, deleted = id });
    }

    [McpServerTool(Name = "set_enabled", ReadOnly = false, Destructive = false)]
    [Description("Switches a scheduled task on or off. Switching on a task that lets an agent change files puts the question to the operator, and stays off unless they allow it.")]
    public async Task<string> SetEnabledAsync(
        [Description("The task id.")] string id,
        [Description("true to switch it on, false to switch it off.")] bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (_RefuseTaskRun() is { } refused)
        {
            return refused;
        }

        if (_Find(id) is not { } task)
        {
            return _Fail($"No scheduled task with id '{id}'.");
        }

        if (enabled && !task.IsEnabled && AgentPromptAction.IsDangerous(task))
        {
            var request = new ConsentRequest(
                $"Schedule wants to switch on: {task.Name}",
                $"Switch on the scheduled task '{task.Name}' ({task.Schedule}, {task.TimeZone}), {(task.RunUnattended ? "running without asking each time" : "asking each time it runs")}:\n{AgentPromptAction.Describe(task)}",
                new ConsentSource(null, null, "Schedules"),
                "schedules.enable",
                ConsentRisk.Dangerous);
            if (await AgentPromptAction.AskAsync(host, request, AgentPromptAction.ConsentWait, cancellationToken) is { } why)
            {
                return _Fail($"Denied: the operator did not allow it ({why}), so the task stays off.");
            }

            // The operator allowed what the card showed; a task changed while they were asked is not that task.
            if (_Find(id) is not { } now || AgentPromptAction.Describe(now) != AgentPromptAction.Describe(task) || now.Schedule != task.Schedule)
            {
                return _Fail("The task changed while the operator was asked, so it stays off. Ask again.");
            }

            task = now;
        }

        return _Save(task with { IsEnabled = enabled }, replacing: id, keepEnabled: true);
    }

    [McpServerTool(Name = "run_now", ReadOnly = false, Destructive = true)]
    [Description("Runs a scheduled task now, outside its schedule, and returns at once with the run's id; read the outcome with list_runs. A task that lets an agent change files asks the operator first, every time.")]
    public string RunNow([Description("The task id.")] string id)
    {
        if (_RefuseTaskRun() is { } refused)
        {
            return refused;
        }

        return engine.RunNow(id, RunTrigger.Mcp) is { } run
            ? _Serialize(new { ok = true, runId = run.Id, outcome = run.Outcome, reason = run.Reason })
            : _Fail($"No scheduled task with id '{id}'.");
    }

    // ── runs ───────────────────────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_runs", ReadOnly = true)]
    [Description("Lists runs, newest first: when, why (schedule, catch-up, by hand, over MCP), the outcome and its reason. The output itself is in get_run_output.")]
    public string ListRuns(
        [Description("Only this task's runs; leave out for all.")] string? taskId = null,
        [Description("At most this many, 20 by default.")] int limit = 20) =>
        _Serialize(runs.Load()
            .Where(run => taskId is null || run.TaskId == taskId)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(run => new { run.Id, run.TaskId, run.ScheduledFor, run.Trigger, run.Outcome, run.Reason, run.StartedAt, run.Duration, run.DelegatedTaskId }));

    [McpServerTool(Name = "get_run_output", ReadOnly = true)]
    [Description("Returns what one run produced, by its id from list_runs. An agent run also names its delegated task, whose transcript is in the delegated-tasks view.")]
    public string GetRunOutput([Description("The run id.")] string runId) =>
        runs.Load().FirstOrDefault(run => run.Id == runId) is { } found
            ? _Serialize(new { found.Id, found.Outcome, found.Reason, found.Output, found.DelegatedTaskId })
            : _Fail($"No run with id '{runId}'.");

    [McpServerTool(Name = "list_delivery_channels", ReadOnly = true)]
    [Description("Lists where a task's result can go right now: the cockpit notification and each open channel (Discord, …) with its targets.")]
    public string ListDeliveryChannels() =>
        _Serialize(delivery.Choices().Select(channel => new
        {
            id = channel.Id,
            name = channel.DisplayName,
            targets = channel.Targets.Select(target => new { id = target.Id, name = target.DisplayName }),
        }));

    // ── reminders (R1, AC-1549) ────────────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "create_reminder", ReadOnly = false, Destructive = false)]
    [Description("Creates a reminder for the operator." + TimeRules)]
    public string CreateReminder(
        [Description("What to remind them of.")] string text,
        [Description("When, as ISO-8601 local time, e.g. '2026-10-23T08:00'.")] string at,
        [Description("An IANA time zone.")] string timeZone) => _Fail(NotAvailableYet);

    [McpServerTool(Name = "list_reminders", ReadOnly = true)]
    [Description("Lists the operator's reminders.")]
    public string ListReminders() => _Fail(NotAvailableYet);

    [McpServerTool(Name = "snooze_reminder", ReadOnly = false, Destructive = false)]
    [Description("Snoozes a reminder.")]
    public string SnoozeReminder([Description("The reminder id.")] string id, [Description("Minutes to snooze.")] int minutes) =>
        _RefuseTaskRun() ?? _Fail(NotAvailableYet);

    [McpServerTool(Name = "complete_reminder", ReadOnly = false, Destructive = false)]
    [Description("Marks a reminder done.")]
    public string CompleteReminder([Description("The reminder id.")] string id) => _RefuseTaskRun() ?? _Fail(NotAvailableYet);

    [McpServerTool(Name = "delete_reminder", ReadOnly = false, Destructive = true)]
    [Description("Deletes a reminder.")]
    public string DeleteReminder([Description("The reminder id.")] string id) => _RefuseTaskRun() ?? _Fail(NotAvailableYet);

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    // AC-38: a run a task started gets the read tools and create_reminder, nothing that changes a task.
    private string? _RefuseTaskRun() => agent.IsTaskRun(host.CurrentMcpCallerPaneId) ? _Fail(TaskRunsMayNot) : null;

    private ScheduledTask? _Find(string id) => tasks.Load()?.FirstOrDefault(task => task.Id == id);

    // `deliverTo` is checked against the open channels and their targets; left out, `unchanged` stands as it is, so a
    // task whose channel is closed right now can still be renamed.
    private (ScheduledTask Task, string? Error) _Build(
        ScheduledTask task, string schedule, string timeZone, string action, string text, string? profile, string? workingDirectory,
        string permission, TimeSpan? timeout, string[]? deliverTo, IReadOnlyList<DeliveryTarget> unchanged)
    {
        var normalized = _Normalize(schedule);
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone))
        {
            return (task, $"'{timeZone}' is not a time zone this machine knows. Use an IANA name such as 'Europe/Amsterdam'.");
        }

        if (TaskSchedule.TryParse(normalized, zone) is null)
        {
            return (task, $"'{schedule}' is not a schedule: use a 5-field cron, 'once 2026-10-23T08:00' or 'every 15 m'.");
        }

        var kind = action switch
        {
            "notification" => TaskActionKind.Notification,
            "agent_prompt" => TaskActionKind.AgentPrompt,
            _ => (TaskActionKind?)null,
        };
        if (kind is not { } actionKind || string.IsNullOrWhiteSpace(text) || (actionKind == TaskActionKind.AgentPrompt && string.IsNullOrWhiteSpace(profile)))
        {
            return (task, "A task needs an action ('notification' or 'agent_prompt') with its text, and an agent prompt a profile too.");
        }

        if (permission is not ("read_only" or "write"))
        {
            return (task, $"'{permission}' is not a permission: use 'read_only' or 'write'.");
        }

        var targets = unchanged;
        if (deliverTo is not null)
        {
            if (_Targets(deliverTo) is not { } parsed)
            {
                return (task, "Delivery is 'cockpit', or a channel from list_delivery_channels, optionally with one of its targets as 'discord:dm'.");
            }

            targets = parsed;
        }

        return (task with
        {
            Schedule = normalized,
            TimeZone = timeZone,
            Action = new TaskAction(actionKind, text.Trim())
            {
                Profile = actionKind == TaskActionKind.AgentPrompt ? profile?.Trim() : null,
                WorkingDirectory = actionKind == TaskActionKind.AgentPrompt ? workingDirectory?.Trim() : null,
                Permission = actionKind == TaskActionKind.AgentPrompt && permission == "write" ? AgentPermission.Write : AgentPermission.ReadOnly,
                Timeout = timeout,
            },
            Delivery = targets,
        }, null);
    }

    // Null when an entry names no open channel, or a target that channel does not have.
    private IReadOnlyList<DeliveryTarget>? _Targets(string[] entries)
    {
        var open = delivery.Choices();
        List<DeliveryTarget> targets = [];
        foreach (var entry in entries)
        {
            var parts = (entry ?? string.Empty).Split(':', 2);
            var channel = open.FirstOrDefault(candidate => candidate.Id == parts[0].Trim());
            var target = parts.Length > 1 ? parts[1].Trim() : null;
            if (channel is null || (target is not null && channel.Targets.All(candidate => candidate.Id != target)))
            {
                return null;
            }

            targets.Add(new DeliveryTarget(channel.Id, target));
        }

        return targets.Count == 0 ? null : targets;
    }

    // A Dangerous task never comes on through a change an agent made: only set_enabled, through the operator.
    private string _Save(ScheduledTask task, string? replacing, bool keepEnabled = false)
    {
        var needsApproval = !keepEnabled && AgentPromptAction.IsDangerous(task);
        var saved = needsApproval ? task with { IsEnabled = false } : task;
        lock (_gate)
        {
            if (tasks.Load() is not { } all)
            {
                return _Fail("The stored task list cannot be read.");
            }

            tasks.Save(replacing is null ? [.. all, saved] : [.. all.Select(existing => existing.Id == replacing ? saved : existing)]);
        }

        return _Serialize(new
        {
            ok = true,
            id = saved.Id,
            enabled = saved.IsEnabled,
            note = needsApproval ? NeedsApproval : null,
            schedule = saved.Schedule,
            timeZone = saved.TimeZone,
            readsAs = CronText.Describe(saved.Schedule),
        });
    }

    // ISO-8601 local time for a one-off, in the engine's own 'once yyyy-MM-dd HH:mm'. Only that shape: a 'Z' or an
    // offset would put the moment in another zone than the task's, so it is left for the engine to refuse.
    private static string _Normalize(string schedule)
    {
        var text = schedule.Trim();
        return text.StartsWith("once ", StringComparison.OrdinalIgnoreCase)
            && DateTime.TryParseExact(text[5..].Trim(), ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? $"once {local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}"
            : text;
    }

    private static object _Summary(ScheduledTask task) => new
    {
        task.Id,
        task.Name,
        task.Schedule,
        task.TimeZone,
        readsAs = CronText.Describe(task.Schedule),
        enabled = task.IsEnabled,
        action = task.Action.Kind,
        dangerous = AgentPromptAction.IsDangerous(task),
        delivery = task.Delivery.Select(target => target.Target is null ? target.Channel : $"{target.Channel}:{target.Target}"),
    };

    private static string _Serialize(object value) => JsonSerializer.Serialize(value, Json);

    private static string _Fail(string error) => _Serialize(new { ok = false, error });
}
