namespace Zyra.Voice.Infrastructure.Claude;

/// <summary>
/// Thin seam over a spawned <c>claude</c> CLI process so <see cref="ClaudeCliSession"/> can be
/// unit-tested without starting a real process. Line-oriented, matching stream-json framing:
/// one JSON object per stdout line in, one JSON object per stdin line out.
/// </summary>
internal interface IClaudeCliProcess : IAsyncDisposable
{
    /// <summary>Starts the underlying process. Must be called exactly once.</summary>
    void Start();

    /// <summary>Writes a single line (without trailing newline) to the process's stdin and flushes.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The process's stdout, split into lines, completing when the process exits.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }
}
