namespace Cockpit.Core.Abstractions.Verify;

/// <summary>
/// The outcome of running a verify runner's command (AC-86): its exit code, the output it printed, how long it
/// took, and whether it was killed for running past the timeout. Fail-soft — a non-zero exit or a timeout is a
/// result to report, not an exception to throw, so the tool can tell the agent what happened.
/// </summary>
/// <param name="ExitCode">The process exit code; -1 when it was killed on timeout.</param>
/// <param name="StandardOutput">Everything the command wrote to stdout.</param>
/// <param name="StandardError">Everything the command wrote to stderr.</param>
/// <param name="Duration">How long the command ran before exiting or being killed.</param>
/// <param name="TimedOut">True when the command was killed for exceeding the timeout.</param>
public sealed record VerifyRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut);
