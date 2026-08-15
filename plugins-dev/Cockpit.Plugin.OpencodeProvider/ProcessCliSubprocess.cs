using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: a copy of Kimi's own ProcessCliSubprocess, unchanged — only which CLI it spawns differs. Exercised
// live against the real opencode binary in this session's research, unlike Kimi's own copy of this class.
internal sealed class ProcessCliSubprocess : ICliSubprocess
{
    // A hard cap on a single stdout/stderr line — untrusted output with no newline would otherwise grow the
    // buffer unbounded. 16 MiB is generous; the largest observed live payload was under 100 KB.
    internal const int MaxLineLengthChars = 16 * 1024 * 1024;

    private Process? _process;
    private bool _started;
    private bool _disposed;
    private int _droppedOversizedLineCount;

    public int? ProcessId => !_disposed && _started && _process is { HasExited: false } process ? process.Id : null;

    // How many stdout/stderr lines were dropped for exceeding MaxLineLengthChars — a caller can inspect
    // this rather than the drop happening with no trace anywhere.
    public int DroppedOversizedLineCount => _droppedOversizedLineCount;

    public bool HasExited => _disposed || (_started && (_process?.HasExited ?? true));

    public int? ExitCode => !_disposed && _started && _process is { HasExited: true } process ? process.ExitCode : null;

    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // opencode acp speaks UTF-8 on all three streams; without pinning the redirected streams .NET
            // decodes with the OS default code page (ANSI/OEM on Windows) and mangles non-ASCII output —
            // the same mojibake bug the Kimi/Claude/Codex process wrappers pin against.
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in environmentVariables)
        {
            if (value is null)
            {
                startInfo.EnvironmentVariables.Remove(name);
            }
            else
            {
                startInfo.EnvironmentVariables[name] = value;
            }
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Start();
        _started = true;
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var process = _RequireStartedProcess();
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default) =>
        _ReadLinesAsync(_RequireStartedProcess().StandardOutput, cancellationToken);

    public IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default) =>
        _ReadLinesAsync(_RequireStartedProcess().StandardError, cancellationToken);

    // Test seam (InternalsVisibleTo): exercises the capped line reader against any StreamReader, without
    // spawning a real process.
    internal IAsyncEnumerable<string> ReadLinesAsyncForTests(StreamReader reader, CancellationToken cancellationToken = default) =>
        _ReadLinesAsync(reader, cancellationToken);

    // A capped line accumulator: a line past MaxLineLengthChars is dropped and skipped to the next '\n', so
    // the stream re-synchronises on the next NDJSON line instead of growing without bound.
    private async IAsyncEnumerable<string> _ReadLinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        var isOverCap = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (charsRead == 0)
            {
                if (isOverCap)
                {
                    Interlocked.Increment(ref _droppedOversizedLineCount);
                }
                else if (line.Length > 0)
                {
                    yield return _StripTrailingCarriageReturn(line.ToString());
                }

                yield break;
            }

            for (var index = 0; index < charsRead; index++)
            {
                var current = buffer[index];
                if (current != '\n')
                {
                    if (!isOverCap)
                    {
                        line.Append(current);
                        if (line.Length > MaxLineLengthChars)
                        {
                            line.Clear();
                            isOverCap = true;
                        }
                    }

                    continue;
                }

                if (isOverCap)
                {
                    Interlocked.Increment(ref _droppedOversizedLineCount);
                    isOverCap = false;
                }
                else
                {
                    yield return _StripTrailingCarriageReturn(line.ToString());
                }

                line.Clear();
            }
        }
    }

    private static string _StripTrailingCarriageReturn(string text) =>
        text.Length > 0 && text[^1] == '\r' ? text[..^1] : text;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            // Idempotent: a session's own dispose and an interrupt/crash path can both dispose this
            // instance — a second call must be a safe no-op, not an ObjectDisposedException.
            return;
        }

        _disposed = true;

        if (_started && _process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.Close();
                if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
                {
                    // Kill the entire tree, not just the direct child — matches the Kimi/Claude/Codex
                    // process wrappers' discipline so a stuck opencode never leaves zombie grandchildren
                    // (opencode itself may spawn MCP server subprocesses of its own).
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Close/Kill — not an error.
            }
        }

        _process?.Dispose();
        await Task.CompletedTask;
    }

    private Process _RequireStartedProcess() =>
        _process ?? throw new InvalidOperationException($"{nameof(ProcessCliSubprocess)}.{nameof(Start)} must be called before I/O.");
}
