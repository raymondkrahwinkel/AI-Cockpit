using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules;

// Schedules (AC-1539): scheduled tasks on an engine of their own, not on Workflows (AC-885). This is the backend
// part, so the engine runs on the desktop with the plugin window closed and on a headless server alike (plan §7).
// The screen is A5; it hangs off the stores and the engine here, as the MCP server cockpit-schedules does (A4).
public sealed class SchedulesPlugin : ICockpitPlugin
{
    private ScheduleEngine? _engine;
    private IReadOnlyList<IDisposable> _handlers = [];

    public PluginMetadata Metadata { get; } = new(
        Id: "schedules",
        DisplayName: "Schedules",
        Author: "Cockpit",
        Description: "Scheduled tasks with their own engine: a cron schedule in a required time zone, a policy per task for runs missed while the machine slept, and every slot fired at most once. A task delivers a fixed notification or an agent's answer to a prompt.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new SchedulesSettings(host.Storage);
        var delivery = new TaskDelivery(host);
        var agentPrompt = new AgentPromptAction(host, delivery, AgentPromptAction.ConsentWait);
        var tasks = new TaskStore(host.Storage);
        var runs = new RunStore(host.Cache, TimeProvider.System, run => SchedulesChannelHandlers.Publish(host, run));
        var reminders = new ReminderService(host, host.Storage, delivery, TimeProvider.System,
            due => host.Channel.Publish(SchedulesChannel.RemindersDue, System.Text.Json.JsonSerializer.SerializeToElement(due, SchedulesChannel.Json)));
        _engine = new ScheduleEngine(
            tasks,
            new MarkStore(host.Cache),
            runs,
            (task, trigger, cancellationToken) => task.Action.Kind == TaskActionKind.AgentPrompt
                ? agentPrompt.AdmitAsync(task, trigger, cancellationToken)
                : Task.FromResult<string?>(null),
            (task, _, cancellationToken) => task.Action.Kind == TaskActionKind.AgentPrompt
                ? agentPrompt.RunAsync(task, cancellationToken)
                : delivery.DeliverAsync(task, task.Action.Text, cancellationToken),
            () => settings.MaxConcurrentRuns,
            TimeProvider.System,
            reminders.TickAsync);

        host.AddHealthSection(_engine.Health);

        // AC-1545: not internal and with no switch of its own, so every session of a profile that keeps the default
        // MCP selection gets it, on the desktop and the server alike; a profile leaves it out in its MCP selection.
        _ = host.AddMcpEndpoint(ScheduleMcpTools.EndpointName, new ScheduleMcpTools(host, tasks, runs, _engine, agentPrompt, delivery, reminders));

        // AC-1546: the screen's operations, over the channel the UI part (or a remote one) talks to.
        _handlers = new SchedulesChannelHandlers(host, tasks, runs, _engine, agentPrompt, delivery, settings, TimeProvider.System).Register();
    }

    public void Dispose()
    {
        foreach (var handler in _handlers)
        {
            handler.Dispose();
        }

        _engine?.Dispose();
    }
}
