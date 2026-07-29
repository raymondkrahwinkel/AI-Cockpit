namespace Cockpit.Plugin.LocalCi.Execution;

/// <summary>
/// Runs a tool that talks for minutes rather than milliseconds, handing every line over as it appears.
/// <para>
/// Deliberately not <see cref="Runtime.ICliRunner"/> with a longer timeout: that one answers "what did it say",
/// which means holding the whole log until the end and putting a deadline on it. A workflow job has no honest
/// deadline — a cold restore is slow and a fast one is fast — and its output is worth nothing after the fact if
/// nobody could watch it happen.
/// </para>
/// </summary>
internal interface IStreamingCliRunner
{
    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> as argv (never through a shell) and
    /// calls <paramref name="onLine"/> for each line of its output, stdout and stderr alike — act writes its
    /// progress to stderr, so a caller reading only stdout would watch an empty log. Cancelling kills the process
    /// tree and throws; a tool that is not installed comes back as <see cref="StreamedRun.Started"/> <c>false</c>.
    /// </summary>
    Task<StreamedRun> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onLine,
        CancellationToken cancellationToken);
}

/// <param name="Started">False when the executable could not be launched — it is not installed or not on PATH.</param>
internal sealed record StreamedRun(bool Started, int ExitCode)
{
    public static StreamedRun NotStarted { get; } = new(Started: false, ExitCode: -1);

    public bool Succeeded => Started && ExitCode == 0;
}
