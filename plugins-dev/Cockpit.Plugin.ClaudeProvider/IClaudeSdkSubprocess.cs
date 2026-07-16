namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Thin seam over the single, long-lived <c>claude</c> child process the SDK-route driver spawns (Fase 4) — the
/// plugin-local mirror of the host's <c>Cockpit.Infrastructure.Sessions.IClaudeCliProcess</c>. Unlike the CLI-agent
/// plugin's per-turn <c>ICliSubprocess</c>, this process is persistent: one spawn hosts the whole multi-turn session,
/// fed one JSON line per stdin write and read one line at a time off stdout. Kept as a mockable seam so
/// <see cref="ClaudeSdkSessionDriver"/>'s turn-taking and control-protocol logic is unit-tested against a fake, since
/// no logged-in <c>claude</c> CLI exists in this sandbox.
/// </summary>
internal interface IClaudeSdkSubprocess : IAsyncDisposable
{
    /// <summary>Starts the process. Must be called exactly once before any I/O.</summary>
    void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables);

    /// <summary>Writes a single line (without trailing newline) to stdin and flushes — one stream-json object per line.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The process's stdout, split into lines, completing when the process exits.</summary>
    IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The process's stderr, split into lines, completing when the process exits. Must be drained concurrently with
    /// stdout — a full, unread stderr pipe would deadlock the child. (The host's persistent process never read its own
    /// stderr; draining it here is the belt-and-braces the host relied on the pipe buffer never filling for.)
    /// </summary>
    IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>The OS process id once started; <see langword="null"/> before start or after exit/dispose (#78, D10).</summary>
    int? ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }
}
