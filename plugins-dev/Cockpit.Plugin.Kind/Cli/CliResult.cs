namespace Cockpit.Plugin.Kind.Cli;

// What one CliRunner.RunAsync call produced. Exit code decides success — stderr text alone never flips a zero exit
// code to a failure, since kind writes its own progress chatter there on a clean run.
// `Started`: false when the executable was not found. `TimedOut`: true when it ran past the deadline.
internal sealed record CliResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string Stdout,
    string Stderr)
{
    public static CliResult NotStarted { get; } = new(Started: false, TimedOut: false, ExitCode: -1, string.Empty, string.Empty);

    public static CliResult Timeout { get; } = new(Started: true, TimedOut: true, ExitCode: -1, string.Empty, string.Empty);

    public static CliResult Exited(int exitCode, string stdout, string stderr) =>
        new(Started: true, TimedOut: false, exitCode, stdout, stderr);

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}
