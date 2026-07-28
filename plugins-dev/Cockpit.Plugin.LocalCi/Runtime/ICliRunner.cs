namespace Cockpit.Plugin.LocalCi.Runtime;

/// <summary>
/// Runs a command-line tool and hands back everything the detection needs to tell the three Docker states apart:
/// whether the executable existed at all, what it said, and whether it answered within the time we allow it.
/// An interface because the interesting cases — no Docker, a dead engine, a pipe that never answers — cannot be
/// produced on a developer's machine on demand.
/// </summary>
internal interface ICliRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> as argv (never through a shell) and
    /// waits at most <paramref name="timeout"/> for it. The two outcomes a detection has to tell apart are results
    /// rather than exceptions: a tool that is not installed comes back as <see cref="CliResult.Started"/> <c>false</c>,
    /// one that hangs as <see cref="CliResult.TimedOut"/>. Cancelling <paramref name="cancellationToken"/> throws.
    /// </summary>
    Task<CliResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>What a <see cref="ICliRunner.RunAsync"/> call produced.</summary>
/// <param name="Started">False when the executable could not be launched — it is not installed or not on PATH.</param>
/// <param name="TimedOut">True when it launched but did not finish within the timeout.</param>
internal sealed record CliResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string StandardOutput,
    string StandardError)
{
    public static CliResult NotStarted { get; } = new(Started: false, TimedOut: false, ExitCode: -1, string.Empty, string.Empty);

    public static CliResult Timeout { get; } = new(Started: true, TimedOut: true, ExitCode: -1, string.Empty, string.Empty);

    public static CliResult Exited(int exitCode, string standardOutput, string standardError) =>
        new(Started: true, TimedOut: false, exitCode, standardOutput, standardError);

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}
