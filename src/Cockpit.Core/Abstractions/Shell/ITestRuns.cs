namespace Cockpit.Core.Abstractions.Shell;

public enum TestRunState
{
    Running,
    Queued,
    Passed,
    Failed,
    Stopped,
}

// AC-2028: one test run as the status bar's dialog shows it. A heavy run that is also a tracked run is one entry.
public sealed record TestRunEntry(
    string RunId,
    string Command,
    TestRunState State,
    bool IsHeavy,
    string? Kind,
    string? Session,
    string? WorkingDirectory,
    string Note,
    string Output,
    Func<Task>? StopAsync)
{
    public bool IsActive => State is TestRunState.Running or TestRunState.Queued;
}

// AC-2028: the runs `start_run` began, running, queued and recently finished, tracked and heavy together.
public interface ITestRuns
{
    IReadOnlyList<TestRunEntry> Entries();

    event Action? Changed;
}

// Marks a supervised-activity source that ITestRuns already shows, so the status bar's generic host skips it.
public interface ITestRunSource;
