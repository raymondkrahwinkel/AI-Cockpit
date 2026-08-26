namespace Cockpit.Core.Abstractions.Shell;

// AC-1094: what one tracked run produced. `TimedOut` carries whatever output the command had already printed —
// never silence, and nothing is restarted on its behalf.
public sealed record TrackedRunResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool TimedOut);

/// <summary>
/// Runs one command as a child process and, once it ends — by finishing or by the timeout — ends every process its
/// tree still holds, including one a build server left behind after being reparented to pid 1 (AC-1094). A seam of
/// its own so the MCP tool can be tested without spawning a real process.
/// </summary>
public interface ITrackedCommandRunner
{
    Task<TrackedRunResult> RunAsync(
        string workingDirectory,
        string command,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string runId,
        CancellationToken cancellationToken = default);
}
