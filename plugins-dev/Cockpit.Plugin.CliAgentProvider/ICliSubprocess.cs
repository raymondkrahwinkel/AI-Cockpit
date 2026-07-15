namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Thin seam over a spawned CLI-agent child process (#45 fase B1) — the plugin-local mirror of
/// <c>Cockpit.Infrastructure.Sessions.IClaudeCliProcess</c>, adapted for a proces-per-turn agent CLI (Codex)
/// instead of Claude's single persistent process: <see cref="ReadStderrLinesAsync"/> is new here because
/// Codex writes turn progress to stderr, and a full, undrained stderr pipe would deadlock the child (see the
/// design doc §4 "stderr-deadlock" caveat) — Claude's driver never needed to read its own stderr at all.
/// <see cref="CliSubprocessPluginSessionDriver"/> spawns one instance of this per turn.
/// </summary>
internal interface ICliSubprocess : IAsyncDisposable
{
    /// <summary>
    /// Starts the underlying process. Must be called exactly once per instance — this seam is one-shot,
    /// matching the proces-per-turn lifecycle (a new <see cref="ICliSubprocess"/> is created for every turn).
    /// </summary>
    void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables);

    /// <summary>Writes a single line (without trailing newline) to the process's stdin and flushes — used only in <c>PromptMode="stdin"</c>.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The process's stdout, split into lines, completing when the process exits.</summary>
    IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The process's stderr, split into lines, completing when the process exits. Must be drained
    /// concurrently with <see cref="ReadStdoutLinesAsync"/> by a separate task — never left unread.
    /// </summary>
    IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>The OS process id once the process has started; <see langword="null"/> before start or after dispose (D10).</summary>
    int? ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>The process's exit code, once <see cref="HasExited"/> is true; <see langword="null"/> before that.</summary>
    int? ExitCode { get; }
}
