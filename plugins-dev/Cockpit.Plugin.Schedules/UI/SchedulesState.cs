using System.Text.Json;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// What the screen shows, kept from the backend's snapshot and its RunRecorded events, so a run started here goes from
// "Running…" to its outcome without a reload. Every change goes over the channel, never to the engine (A6 reaches a server).
internal sealed class SchedulesState : IDisposable
{
    private readonly IPluginUiChannel _channel;
    private readonly IDisposable _subscription;

    public SchedulesState(IPluginUiChannel channel)
    {
        _channel = channel;
        _subscription = channel.Subscribe("nothing", _OnRunRecorded);
    }

    public event Action? Changed;

    public List<SchedulesTaskView> Tasks { get; private set; } = [];

    public List<RunRecord> Runs { get; private set; } = [];

    public List<OperatorMessageChannelInfo> Channels { get; private set; } = [];

    public SchedulesSettingsView Settings { get; private set; } = new(2, "UTC", 30);

    public async Task RefreshAsync()
    {
        var snapshot = await AskAsync<SchedulesSnapshot>(SchedulesChannel.Snapshot);
        if (snapshot is not null)
        {
            Tasks = snapshot.Tasks;
            Runs = snapshot.Runs;
            Channels = snapshot.Channels;
            Settings = snapshot.Settings;
        }

        Changed?.Invoke();
    }

    // The newest run of a task, as the list's "Last run" shows it.
    public RunRecord? LastRun(string taskId) => Runs.FirstOrDefault(run => run.TaskId == taskId && run.Outcome != RunOutcome.Missed);

    public async Task<RunRecord?> RunNowAsync(string taskId)
    {
        var run = await AskAsync<RunRecord>(SchedulesChannel.RunNow, new SchedulesTaskRequest(taskId));
        if (run is not null)
        {
            _Upsert(run);
        }

        return run;
    }

    public async Task<TResult?> AskAsync<TResult>(string action, object? request = null)
    {
        var payload = request is null
            ? JsonSerializer.SerializeToElement(new { }, SchedulesChannel.Json)
            : JsonSerializer.SerializeToElement(request, request.GetType(), SchedulesChannel.Json);
        var answer = await _channel.InvokeAsync(action, payload);
        return answer.Deserialize<TResult>(SchedulesChannel.Json);
    }

    public void Dispose() => _subscription.Dispose();

    private void _OnRunRecorded(PluginChannelEvent channelEvent)
    {
        if (channelEvent.Payload.Deserialize<RunRecord>(SchedulesChannel.Json) is { } run)
        {
            _Upsert(run);
        }
    }

    // The channel raises on its own thread; the views redraw on the UI thread.
    private void _Upsert(RunRecord run) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        var index = Runs.FindIndex(existing => existing.Id == run.Id);
        if (index >= 0)
        {
            Runs[index] = run;
        }
        else
        {
            Runs.Insert(0, run);
        }

        Changed?.Invoke();
    });
}
