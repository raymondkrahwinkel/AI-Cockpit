using System.Reflection;
using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// The server switch for `--screenshot --scene plugin:<name>` (AC-1547): this computer has one task, homeserver the
// AI-Hub's, one of them running. A dev hook, not a plugin contract; the channels answer synchronously from fixed data.
internal static class SchedulesScenes
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 14, 32, 0, TimeSpan.Zero);

    public static IReadOnlyDictionary<string, Func<Control>> Views { get; } = new Dictionary<string, Func<Control>>
    {
        ["schedules-server"] = () => _Screen(new PluginServerTarget("homeserver", SceneHost.Create(_Server(), ["ai-hub"], ["/srv/repos/ai-os"]))),
        ["schedules-server-unavailable"] = () => _Screen(new PluginServerTarget("homeserver", null, "homeserver has Schedules 0.6.7, this computer 0.7.0.")),
    };

    private static Control _Screen(PluginServerTarget server)
    {
        var host = SceneHost.Create(new SceneChannel(new SchedulesSnapshot([_Task("local-1", "Water the plants", "0 8 * * *")], [], [], new(2, "Europe/Amsterdam", 30), Now)), ["personal"], []);
        var state = new SchedulesState(host.Channel);
        _Done(state.RefreshAsync());
        var screen = new SchedulesScreen(state, host, ["personal"], servers: [server]);
        _Done(screen.SwitchAsync(1));
        return screen;
    }

    private static SceneChannel _Server()
    {
        var digest = _Task("hub-1", "Morning digest", "0 7 * * 1-5");
        var inbox = _Task("hub-2", "Inbox triage", "*/30 8-18 * * 1-5");
        var running = new RunRecord { Id = "run-1", TaskId = "hub-2", Trigger = RunTrigger.Schedule, Outcome = RunOutcome.Running, StartedAt = Now.AddMinutes(-2) };
        var done = new RunRecord { Id = "run-0", TaskId = "hub-1", Trigger = RunTrigger.Schedule, Outcome = RunOutcome.Succeeded, StartedAt = Now.AddHours(-7.5), Duration = TimeSpan.FromSeconds(48) };
        return new SceneChannel(new SchedulesSnapshot([digest, inbox], [running, done], [], new(2, "Europe/Amsterdam", 30), Now));
    }

    private static SchedulesTaskView _Task(string id, string name, string cron) => new(
        new ScheduledTask { Id = id, Name = name, Schedule = cron, TimeZone = "Europe/Amsterdam", Action = new TaskAction(TaskActionKind.Notification, name) },
        null,
        Now.AddHours(1));

    // The stub channels answer at once, so a task still running is a broken scene.
    private static void _Done(Task task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("A scene's stub channel must answer synchronously and without fault.", task.Exception);
        }
    }

    private sealed class SceneChannel(SchedulesSnapshot snapshot) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(action == SchedulesChannel.Snapshot ? JsonSerializer.SerializeToElement(snapshot, SchedulesChannel.Json) : JsonSerializer.SerializeToElement<object?>(null));

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    // Every host member answers its default, except the channel, the profiles and the projects the screen reads.
    public class SceneHost : DispatchProxy
    {
        private IPluginUiChannel? _channel;
        private IReadOnlyList<PluginProfileInfo> _profiles = [];
        private IReadOnlyList<PluginProjectInfo> _projects = [];

        public static ICockpitUiHost Create(IPluginUiChannel channel, IReadOnlyList<string> profiles, IReadOnlyList<string> folders)
        {
            var host = DispatchProxy.Create<ICockpitUiHost, SceneHost>();
            var scene = (SceneHost)(object)host;
            scene._channel = channel;
            scene._profiles = [.. profiles.Select(label => new PluginProfileInfo(label, "claude", ""))];
            scene._projects = [.. folders.Select(folder => new PluginProjectInfo(folder, Path.GetFileName(folder)) { SourceDirectory = folder })];
            return host;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Channel" => _channel,
            "GetProfilesAsync" => Task.FromResult(_profiles),
            "GetProjectsAsync" => Task.FromResult(_projects),
            "GetServerTargetsAsync" => Task.FromResult<IReadOnlyList<PluginServerTarget>>([]),
            _ => targetMethod?.ReturnType is { IsValueType: true } type && type != typeof(void) ? Activator.CreateInstance(type) : targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null,
        };
    }
}
