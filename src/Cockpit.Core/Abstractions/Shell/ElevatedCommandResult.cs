namespace Cockpit.Core.Abstractions.Shell;

// AC-1335: what one elevated command produced. `Output` is whatever the command wrote to its output file so far —
// on a timeout that may be partial; `ExitCode` is null unless the process ended. `Error` explains a Failed outcome.
public sealed record ElevatedCommandResult(ElevatedCommandOutcome Outcome, int? ExitCode, string Output, string? Error = null);
