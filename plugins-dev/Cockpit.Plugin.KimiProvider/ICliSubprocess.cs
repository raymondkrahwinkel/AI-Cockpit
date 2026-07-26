namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// Thin seam over a spawned CLI-agent child process (AC-268) — the plugin-local mirror of
/// <c>Cockpit.Plugin.CliAgentProvider.ICliSubprocess</c>, copied rather than shared because plugins cannot
/// reference each other's assemblies (only the host's Abstractions). Unlike Codex's proces-per-turn exec
/// route, <c>kimi acp</c> is spawned once and lives for the whole session, but the seam is identical:
/// <see cref="ReadStderrLinesAsync"/> exists because an undrained stderr pipe deadlocks the child (D14).
/// <see cref="KimiAcpConnection"/> spawns exactly one instance of this per session.
/// </summary>
internal interface ICliSubprocess : IAsyncDisposable
{
    /// <summary>Starts the underlying process. Must be called exactly once per instance.</summary>
    void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables);

    /// <summary>Writes a single line (without trailing newline) to the process's stdin and flushes.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The process's stdout, split into lines, completing when the process exits.</summary>
    IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The process's stderr, split into lines, completing when the process exits. Must be drained
    /// concurrently with <see cref="ReadStdoutLinesAsync"/> by a separate task — never left unread.
    /// </summary>
    IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>The OS process id once the process has started; <see langword="null"/> before start or after dispose.</summary>
    int? ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>The process's exit code, once <see cref="HasExited"/> is true; <see langword="null"/> before that.</summary>
    int? ExitCode { get; }
}
