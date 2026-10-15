using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules;

// Schedules (AC-1539): scheduled tasks on an engine of their own, not on Workflows (AC-885). This is the backend
// part, so the engine runs on the desktop with the plugin window closed and on a headless server alike (plan §7).
// MCP, the agent action, delivery channels and the screen are A2–A5; they hang off the stores and the engine here.
public sealed class SchedulesPlugin : ICockpitPlugin
{
    private ScheduleEngine? _engine;

    public PluginMetadata Metadata { get; } = new(
        Id: "schedules",
        DisplayName: "Schedules",
        Author: "Cockpit",
        Description: "Scheduled tasks with their own engine: a cron schedule in a required time zone, a policy per task for runs missed while the machine slept, and every slot fired at most once. The first action is a fixed notification.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new SchedulesSettings(host.Storage);
        var notification = new NotificationAction(host);
        _engine = new ScheduleEngine(
            new TaskStore(host.Storage),
            new MarkStore(host.Cache),
            new RunStore(host.Cache, TimeProvider.System),
            notification.RunAsync,
            () => settings.MaxConcurrentRuns,
            TimeProvider.System);

        host.AddHealthSection(_engine.Health);
    }

    public void Dispose() => _engine?.Dispose();
}
