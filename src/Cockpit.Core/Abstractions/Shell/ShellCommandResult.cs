namespace Cockpit.Core.Abstractions.Shell;

// AC-1066: outcome of running one shell command: exit code, output, duration, and whether it timed out
// (ExitCode -1 then). Fail-soft, mirroring VerifyRunResult — a non-zero exit or timeout is a result to report to
// the calling agent, not an exception to throw.
public sealed record ShellCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut);
