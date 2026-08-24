namespace Cockpit.Plugin.Kubernetes.Helm;

/// <summary>
/// Runs a built <see cref="HelmCommand"/> as a real process and hands back its exit code, stdout and stderr.
/// An interface so a fake can stand in for the tests that must not depend on a helm binary being on PATH.
/// </summary>
internal interface IHelmRunner
{
    Task<HelmResult> RunAsync(HelmCommand command, TimeSpan timeout, CancellationToken cancellationToken = default);
}

// What one `IHelmRunner.RunAsync` call produced. Exit code decides success — helm writes deprecation warnings and
// even NOTES.txt to stderr on exit 0, so `Succeeded` never looks at stderr content (AC 10). `Started`: false when
// not installed/not on PATH. `TimedOut`: true when it ran past the deadline.
internal sealed record HelmResult(
    bool Started,
    bool TimedOut,
    int ExitCode,
    string Stdout,
    string Stderr)
{
    public static HelmResult NotStarted { get; } = new(Started: false, TimedOut: false, ExitCode: -1, string.Empty, string.Empty);

    public static HelmResult Timeout { get; } = new(Started: true, TimedOut: true, ExitCode: -1, string.Empty, string.Empty);

    public static HelmResult Exited(int exitCode, string stdout, string stderr) =>
        new(Started: true, TimedOut: false, exitCode, stdout, stderr);

    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}
