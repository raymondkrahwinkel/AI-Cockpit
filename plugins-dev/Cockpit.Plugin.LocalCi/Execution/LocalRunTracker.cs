using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>One finished run, kept so the things that ask about it later cannot disagree about what happened.</summary>
/// <param name="Commit">The checkout's HEAD when the run started, or null when it could not be read. What makes
/// "this ran" answerable as "this ran on the code you are about to push" rather than on something older.</param>
internal sealed record LocalRunRecord(string ProjectRoot, LocalRunResult Result, string? Commit, DateTimeOffset FinishedAt);

/// <summary>
/// What is running now and what happened last in each checkout. Doubles as the status-bar source, so a run started
/// by an agent is visible to the operator with a Kill the operator alone can press — an agent that can start a
/// container on this machine and cannot be stopped by hand is the thing AC-82 exists to prevent.
/// </summary>
internal sealed class LocalRunTracker : ISupervisedActivitySource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalRunRecord> _finished = new(StringComparer.OrdinalIgnoreCase);
    private InFlight? _inFlight;

    public string Label => "Local CI";

    public event Action? Changed;

    public void Begin(string projectRoot, string jobId, DateTimeOffset startedAt, Func<Task> stopAsync)
    {
        lock (_gate)
        {
            _inFlight = new InFlight(Key(projectRoot), jobId, startedAt, stopAsync);
        }

        Changed?.Invoke();
    }

    public void Complete(string projectRoot, LocalRunResult result, string? commit, DateTimeOffset finishedAt)
    {
        var key = Key(projectRoot);
        lock (_gate)
        {
            _finished[key] = new LocalRunRecord(key, result, commit, finishedAt);
            _inFlight = null;
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
        InFlight? running;
        lock (_gate)
        {
            running = _inFlight;
        }

        return running is null
            ? []
            :
            [
                new SupervisedActivity(
                    Id: running.ProjectRoot,
                    Title: $"{running.JobId} (local)",
                    Details:
                    [
                        new ActivityDetail("Checkout", running.ProjectRoot),
                        new ActivityDetail("Running for", _Since(running.StartedAt)),
                    ],
                    StopAsync: running.StopAsync),
            ];
    }

    /// <summary>
    /// One checkout, one key. Paths reach this from a session's working directory, a workflow's folder and an
    /// intent's payload, and those spell the same directory differently often enough that comparing them raw
    /// would file two runs of the same repository under two names.
    /// </summary>
    public static string Key(string projectRoot) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));

    private static string _Since(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{elapsed.Seconds}s";
    }

    private sealed record InFlight(string ProjectRoot, string JobId, DateTimeOffset StartedAt, Func<Task> StopAsync);
}
