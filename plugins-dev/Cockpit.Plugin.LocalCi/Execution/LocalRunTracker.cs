using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.Plugin.LocalCi.Execution;

// One finished run, kept so the things that ask about it later cannot disagree about what happened.
//
// `Commit`: The checkout's HEAD when the run started, or null when it could not be read. What makes
// "this ran" answerable as "this ran on the code you are about to push" rather than on something older.
internal sealed record LocalRunRecord(string ProjectRoot, LocalRunResult Result, string? Commit, DateTimeOffset FinishedAt);

// What is running now and what happened last in each checkout. Doubles as the status-bar source, so a run started
// by an agent is visible to the operator with a Kill the operator alone can press — an agent that can start a
// container on this machine and cannot be stopped by hand is the thing AC-82 exists to prevent.
internal sealed class LocalRunTracker : ISupervisedActivitySource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalRunRecord> _finished = new(StringComparer.OrdinalIgnoreCase);

    // AC-1535: one entry per run, so a second run never takes the first one's Kill out of the status bar.
    private readonly Dictionary<string, InFlight> _inFlight = new(StringComparer.Ordinal);

    public string Label => "Local CI";

    public event Action? Changed;

    public void Begin(string runId, string projectRoot, string jobId, DateTimeOffset startedAt, Func<Task> stopAsync)
    {
        lock (_gate)
        {
            _inFlight[runId] = new InFlight(Key(projectRoot), jobId, startedAt, stopAsync);
        }

        Changed?.Invoke();
    }

    public void Complete(string projectRoot, LocalRunResult result, string? commit, DateTimeOffset finishedAt, string? runId = null)
    {
        var key = Key(projectRoot);
        lock (_gate)
        {
            _finished[key] = new LocalRunRecord(key, result, commit, finishedAt);
            if (runId is not null)
            {
                _inFlight.Remove(runId);
            }
        }

        Changed?.Invoke();
    }

    public LocalRunRecord? LastFor(string projectRoot)
    {
        lock (_gate)
        {
            return _finished.GetValueOrDefault(Key(projectRoot));
        }
    }

    public IReadOnlyList<SupervisedActivity> Snapshot()
    {
        List<KeyValuePair<string, InFlight>> running;
        lock (_gate)
        {
            running = [.. _inFlight];
        }

        return
        [
            .. running.Select(entry => new SupervisedActivity(
                Id: entry.Key,
                Title: $"{entry.Value.JobId} (local)",
                Details:
                [
                    new ActivityDetail("Checkout", entry.Value.ProjectRoot),
                    new ActivityDetail("Running for", _Since(entry.Value.StartedAt)),
                ],
                StopAsync: entry.Value.StopAsync)),
        ];
    }

    // One checkout, one key. Paths reach this from a session's working directory, a workflow's folder and an
    // intent's payload, and those spell the same directory differently often enough that comparing them raw
    // would file two runs of the same repository under two names.
    public static string Key(string projectRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

    private static string _Since(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private sealed record InFlight(string ProjectRoot, string JobId, DateTimeOffset StartedAt, Func<Task> StopAsync);
}
