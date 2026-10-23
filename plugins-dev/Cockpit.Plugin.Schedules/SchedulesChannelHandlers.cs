using System.Text.Json;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules;

// AC-1546: the screen's half of the backend, over the plugin's channel and never on the engine directly, so the same
// screen manages a server's schedules once the channel reaches it (A6). These are the operator's own actions: they
// ask nothing, unlike the same operations over MCP (A4), which an agent makes.
internal sealed class SchedulesChannelHandlers(
    ICockpitHost host, TaskStore tasks, RunStore runs, ScheduleEngine engine, AgentPromptAction agent, TaskDelivery delivery,
    SchedulesSettings settings, TimeProvider time)
{
    private readonly Lock _gate = new();

    public IReadOnlyList<IDisposable> Register() =>
    [
        _Handle<object, SchedulesSnapshot>(SchedulesChannel.Snapshot, _ => Task.FromResult(_Snapshot())),
        _Handle<ScheduledTask, SchedulesResult>(SchedulesChannel.Save, task => Task.FromResult(_Save(task))),
        _Handle<SchedulesTaskRequest, SchedulesResult>(SchedulesChannel.Delete, request => Task.FromResult(_Change(all => [.. all.Where(task => task.Id != request.Id)]))),
        _Handle<SchedulesEnableRequest, SchedulesResult>(SchedulesChannel.SetEnabled, request => Task.FromResult(_Change(all =>
            [.. all.Select(task => task.Id == request.Id ? task with { IsEnabled = request.Enabled, RunUnattended = request.RunUnattended } : task)]))),
        _Handle<SchedulesTaskRequest, RunRecord?>(SchedulesChannel.RunNow, request => Task.FromResult(engine.RunNow(request.Id, RunTrigger.Manual))),
        _Handle<ScheduledTask, RunRecord>(SchedulesChannel.TestRun, _TestRunAsync),
        _Handle<SchedulesPreviewRequest, SchedulesPreview>(SchedulesChannel.Preview, request => Task.FromResult(_Preview(request.Schedule, request.TimeZone))),
        _Handle<SchedulesSettingsView, SchedulesResult>(SchedulesChannel.SaveSettings, view =>
        {
            settings.MaxConcurrentRuns = view.MaxConcurrentRuns;
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(view.DefaultTimeZone, out _))
            {
                return Task.FromResult(new SchedulesResult(false, $"'{view.DefaultTimeZone}' is not a time zone this machine knows."));
            }

            settings.DefaultTimeZone = view.DefaultTimeZone;
            return Task.FromResult(new SchedulesResult(true));
        }),
    ];

    public static void Publish(ICockpitHost host, RunRecord run) =>
        host.Channel.Publish(SchedulesChannel.RunRecorded, JsonSerializer.SerializeToElement(run, SchedulesChannel.Json));

    private SchedulesSnapshot _Snapshot()
    {
        var now = time.GetUtcNow();
        var views = (tasks.Load() ?? []).Select(task =>
        {
            var preview = _Preview(task.Schedule, task.TimeZone, 1, now);
            return new SchedulesTaskView(task, preview.ReadsAs, task.IsEnabled && preview.Next.Count > 0 ? preview.Next[0] : null);
        }).ToList();
        return new SchedulesSnapshot(
            views,
            [.. runs.Load()],
            [.. delivery.Choices()],
            new SchedulesSettingsView(settings.MaxConcurrentRuns, settings.DefaultTimeZone, (int)RunStore.Keep.TotalDays));
    }

    private SchedulesResult _Save(ScheduledTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Name))
        {
            return new SchedulesResult(false, "Give the task a name.");
        }

        if (!_Preview(task.Schedule, task.TimeZone).Valid)
        {
            return new SchedulesResult(false, "Not a valid schedule, or a time zone this machine does not know.");
        }

        var saved = task with { Id = task.Id.Length > 0 ? task.Id : Guid.NewGuid().ToString("n") };
        return _Change(all => all.Any(existing => existing.Id == saved.Id)
            ? [.. all.Select(existing => existing.Id == saved.Id ? saved : existing)]
            : [saved, .. all]) with { Id = saved.Id };
    }

    private SchedulesResult _Change(Func<IReadOnlyList<ScheduledTask>, IReadOnlyList<ScheduledTask>> change)
    {
        lock (_gate)
        {
            if (tasks.Load() is not { } all)
            {
                return new SchedulesResult(false, "The stored task list cannot be read.");
            }

            tasks.Save(change(all));
        }

        return new SchedulesResult(true);
    }

    // Once now with these settings and without saving; its result goes to the cockpit notification only.
    private async Task<RunRecord> _TestRunAsync(ScheduledTask task)
    {
        var test = task with { Delivery = [new DeliveryTarget(DeliveryTarget.CockpitNotification)] };
        var started = time.GetUtcNow();
        var result = test.Action.Kind == TaskActionKind.AgentPrompt
            ? await agent.RunAsync(test, CancellationToken.None)
            : await delivery.DeliverAsync(test, test.Action.Text, CancellationToken.None);
        return new RunRecord
        {
            Id = "test",
            TaskId = task.Id,
            Trigger = RunTrigger.Manual,
            Outcome = result.Outcome,
            Reason = result.Reason,
            Output = result.Output,
            StartedAt = started,
            Duration = time.GetUtcNow() - started,
            DelegatedTaskId = result.DelegatedTaskId,
        };
    }

    private SchedulesPreview _Preview(string schedule, string timeZone, int count = 3, DateTimeOffset? from = null)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var zone) || TaskSchedule.TryParse(schedule, zone) is not { } parsed)
        {
            return new SchedulesPreview(false, null, []);
        }

        var now = from ?? time.GetUtcNow();
        return new SchedulesPreview(true, CronText.Describe(schedule.Trim()) ?? schedule.Trim(), [.. parsed.Slots(now, now.AddYears(5), zone).Take(count)]);
    }

    private IDisposable _Handle<TRequest, TResponse>(string action, Func<TRequest, Task<TResponse>> handle) =>
        host.Channel.Handle(action, async (payload, _) =>
        {
            var request = payload.Deserialize<TRequest>(SchedulesChannel.Json)
                ?? throw new InvalidOperationException($"The '{action}' request was empty.");
            return JsonSerializer.SerializeToElement(await handle(request), SchedulesChannel.Json);
        });
}
