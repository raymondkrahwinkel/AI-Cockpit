namespace Cockpit.Core.Abstractions.Verify;

// AC-1013: Outcome of running a verify runner's command (AC-86): exit code, output, duration, and whether
// it timed out (ExitCode -1 then). Fail-soft — a non-zero exit or timeout is a result to report, not an
// exception to throw, so the tool can tell the agent what happened.
public sealed record VerifyRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut);
