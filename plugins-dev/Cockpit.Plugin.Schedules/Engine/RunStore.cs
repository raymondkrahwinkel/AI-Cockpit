using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Schedules.Engine;

// The run history, in the plugin cache (outside the backup): 30 days per task rather than Workflows' last twenty,
// because the history per task is the point (plan §3). At most 1000 runs per task, so an every-minute task cannot
// grow the shared cache file without bound; output is capped per run, 256 KB counted in characters.
internal sealed class RunStore(IPluginCache cache, TimeProvider time, Action<RunRecord>? recorded = null)
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    public const int MaxOutputChars = 256 * 1024;
    public const int KeepPerTask = 1000;
    public const string Interrupted = "interrupted";

    private const string Key = "runs";
    private readonly Lock _gate = new();

    public IReadOnlyList<RunRecord> Load()
    {
        lock (_gate)
        {
            return _Load();
        }
    }

    // AC-1546: `recorded` hears every run added or changed, so the screen sees "Running…" turn into the outcome.
    public void Add(RunRecord run)
    {
        _Change(runs => runs.Insert(0, _Capped(run)));
        recorded?.Invoke(_Capped(run));
    }

    public void Replace(RunRecord run)
    {
        _Change(runs =>
        {
            var index = runs.FindIndex(existing => existing.Id == run.Id);
            if (index >= 0)
            {
                runs[index] = _Capped(run);
            }
        });
        recorded?.Invoke(_Capped(run));
    }

    // A run still Running at startup was cut off by the restart. Its slot is marked, so it does not fire again.
    public void FailInterrupted() => _Change(runs =>
    {
        for (var index = 0; index < runs.Count; index++)
        {
            if (runs[index].Outcome == RunOutcome.Running)
            {
                runs[index] = runs[index] with { Outcome = RunOutcome.Failed, Reason = Interrupted };
            }
        }
    });

    private void _Change(Action<List<RunRecord>> change)
    {
        lock (_gate)
        {
            var runs = _Load();
            change(runs);
            var oldest = time.GetUtcNow() - Keep;
            var perTask = new Dictionary<string, int>(StringComparer.Ordinal);
            runs.RemoveAll(run => run.StartedAt < oldest || (perTask[run.TaskId] = perTask.GetValueOrDefault(run.TaskId) + 1) > KeepPerTask);
            cache.Set(Key, StoreJson.Write(runs));
        }
    }

    private List<RunRecord> _Load() => StoreJson.Read<List<RunRecord>>(cache.Get<string>(Key)) ?? [];

    private static RunRecord _Capped(RunRecord run) =>
        run.Output is { Length: > MaxOutputChars } output ? run with { Output = output[..MaxOutputChars] } : run;
}
