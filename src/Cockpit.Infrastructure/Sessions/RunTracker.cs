using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.Infrastructure.Sessions;

// AC-1094: what a tracked run is doing right now, or last did — the status-bar source (AC-82), with an
// operator-only Kill per run, and the backing store for `run_status`, the recovery path when a completed run's
// inbox delivery never arrived. Unlike LocalCi's tracker there is no "one at a time" here: nothing serialises
// these runs, so more than one can be in flight together.
internal sealed class RunTracker : ISupervisedActivitySource, ISingletonService
{
    // ponytail: unbounded run ids would grow this dictionary for the life of the cockpit process; a session that
    // starts thousands of runs is not the case this exists for. Raise this, or evict on read, if that changes.
    private const int MaxFinished = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, InFlight> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunRecord> _finished = new(StringComparer.Ordinal);
    private readonly Queue<string> _finishedOrder = new();

    public string Label => "Test runs";

    public event Action? Changed;

    public void Begin(string runId, string command, DateTimeOffset startedAt, Func<Task> stopAsync)
    {
        lock (_gate)
        {
            _running[runId] = new InFlight(runId, command, startedAt, stopAsync);
        }

        Changed?.Invoke();
    }

    public void Complete(string runId, TrackedRunResult result, DateTimeOffset finishedAt)
    {
        lock (_gate)
        {
            _running.Remove(runId);
            _finished[runId] = new RunRecord(runId, result, finishedAt);
            _finishedOrder.Enqueue(runId);
            while (_finishedOrder.Count > MaxFinished)
            {
                _finished.Remove(_finishedOrder.Dequeue());
            }
        }

        Changed?.Invoke();
    }

    public RunRecord? Get(string runId)
    {
        lock (_gate)
        {
            return _finished.GetValueOrDefault(runId);
        }
    }

    public bool IsRunning(string runId)
    {
        lock (_gate)
        {
            return _running.ContainsKey(runId);
        }
    }

    public IReadOnlyList<SupervisedActivity> Snapshot()
    {
        List<InFlight> running;
        lock (_gate)
        {
            running = [.. _running.Values];
        }

        return running
            .Select(run => new SupervisedActivity(
                Id: run.RunId,
                Title: run.Command,
                Details: [new ActivityDetail("Running for", _Since(run.StartedAt))],
                StopAsync: run.StopAsync))
            .ToList();
    }

    private static string _Since(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private sealed record InFlight(string RunId, string Command, DateTimeOffset StartedAt, Func<Task> StopAsync);
}

// AC-1094: one finished run, kept so `run_status` and the inbox delivery it may have missed cannot disagree.
internal sealed record RunRecord(string RunId, TrackedRunResult Result, DateTimeOffset FinishedAt);
