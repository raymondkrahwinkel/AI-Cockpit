using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Formatting;
using Cockpit.Infrastructure.HeavyRuns;
using Cockpit.Plugins.Abstractions.HeavyRuns;

namespace Cockpit.Infrastructure.Sessions;

// AC-2028: the tracked runs (RunTracker) and the heavy-run queue's places as one list. `start_run` gives both the
// same run id, so a run in both is one entry; a heavy run without a tracker (local-ci, maintenance) stands alone.
internal sealed class TestRunBoard : ITestRuns, ISingletonService
{
    private const int MaxFinished = 50;
    private const int MaxOutput = 4000;

    private readonly RunTracker _tracker;
    private readonly HeavyRunQueue _queue;

    public TestRunBoard(RunTracker tracker, HeavyRunQueue queue)
    {
        _tracker = tracker;
        _queue = queue;
    }

    public event Action? Changed
    {
        add
        {
            _tracker.Changed += value;
            _queue.Changed += value;
        }
        remove
        {
            _tracker.Changed -= value;
            _queue.Changed -= value;
        }
    }

    public IReadOnlyList<TestRunEntry> Entries()
    {
        var finished = _tracker.Finished();
        var done = finished.Select(record => record.RunId).ToHashSet(StringComparer.Ordinal);
        var heavy = _queue.Runs().Where(run => !done.Contains(run.RunId)).GroupBy(run => run.RunId).ToDictionary(group => group.Key, group => group.First());
        var entries = new List<TestRunEntry>();

        foreach (var run in _tracker.Running())
        {
            heavy.Remove(run.RunId, out var held);
            entries.Add(new TestRunEntry(
                run.RunId, run.Command, TestRunState.Running, held is not null && held.Kind != HeavyRunKind.Light,
                held?.Kind.ToString().ToLowerInvariant(), held?.PaneId, held?.WorkingDirectory,
                $"Running for {_Since(run.StartedAt)}", string.Empty, run.StopAsync));
        }

        foreach (var run in heavy.Values.OrderByDescending(run => run.Running))
        {
            entries.Add(new TestRunEntry(
                run.RunId, run.Label, run.Running ? TestRunState.Running : TestRunState.Queued, run.Kind != HeavyRunKind.Light,
                run.Kind.ToString().ToLowerInvariant(), run.PaneId, run.WorkingDirectory,
                run.Running ? $"Running for {_Since(run.Since)}" : run.WaitReason ?? "Waiting", string.Empty, run.StopAsync));
        }

        entries.AddRange(finished.Take(MaxFinished).Select(record => new TestRunEntry(
            record.RunId, record.Command, _StateOf(record), false, null, null, null,
            $"Exit code {record.Result.ExitCode}, {Math.Round(record.Result.Duration.TotalSeconds, 1)}s",
            BoundedText.Trim(record.Result.StandardOutput + record.Result.StandardError, MaxOutput), null)));
        return entries;
    }

    private static TestRunState _StateOf(RunRecord record) =>
        record.Stopped ? TestRunState.Stopped : record.Result is { TimedOut: false, ExitCode: 0 } ? TestRunState.Passed : TestRunState.Failed;

    private static string _Since(DateTimeOffset since)
    {
        var elapsed = DateTimeOffset.UtcNow - since;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }
}
