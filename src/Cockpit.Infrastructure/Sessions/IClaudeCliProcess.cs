using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Thin seam over a spawned <c>claude</c> CLI process so <see cref="ClaudeCliSession"/> can be
/// unit-tested without starting a real process. Line-oriented, matching stream-json framing:
/// one JSON object per stdout line in, one JSON object per stdin line out.
/// </summary>
internal interface IClaudeCliProcess : IAsyncDisposable
{
    /// <summary>
    /// Starts the underlying process, optionally under a specific <see cref="SessionProfile"/>
    /// (its own <c>CLAUDE_CONFIG_DIR</c> and, if set, its own executable). Must be called
    /// exactly once. <paramref name="model"/>, when non-null/whitespace, is passed as
    /// <c>--model &lt;value&gt;</c> at launch. <paramref name="enabledMcpServerNames"/> is the
    /// per-session MCP-server selection (#44) fanned out to the <c>--mcp-config</c> this spawn reads;
    /// <see langword="null"/> keeps the pre-#44 behaviour of fanning out the full registry.
    /// <paramref name="workingDirectoryOverride"/>, when non-blank, is the per-session working directory
    /// (New-session dialog) the process starts in, overriding the global option.
    /// </summary>
    void Start(SessionProfile? profile = null, string? permissionMode = null, string? model = null, IReadOnlySet<string>? enabledMcpServerNames = null, string? workingDirectoryOverride = null);

    /// <summary>Writes a single line (without trailing newline) to the process's stdin and flushes.</summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>The process's stdout, split into lines, completing when the process exits.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }
}
