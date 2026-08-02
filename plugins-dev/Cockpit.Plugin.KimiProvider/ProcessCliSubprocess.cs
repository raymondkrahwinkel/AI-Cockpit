using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cockpit.Plugin.KimiProvider;

// Real `ICliSubprocess` backed by `System.Diagnostics.Process` (AC-268) — a copy of
// `Cockpit.Plugin.CliAgentProvider.ProcessCliSubprocess`'s spawn/UTF-8/dispose discipline, adapted for
// a persistent `kimi acp` spawn rather than Codex's proces-per-turn one (the seam itself is identical:
// only `KimiAcpConnection` keeps this instance alive for the whole session instead of one turn).
// Never exercised against a real `kimi` process in this environment — kept as a thin, mockable seam so
// `KimiAcpConnection`'s protocol logic is unit tested against `FakeCliSubprocess` instead;
// the live end-to-end run requires a machine with the `kimi` CLI installed and authenticated (sub [h]).
internal sealed class ProcessCliSubprocess : ICliSubprocess
{
    // P1-9a: a hard cap on a single stdout/stderr line — kimi acp's output is untrusted, and
    // StreamReader.ReadLineAsync (what this used to call) has no length limit of its own: a child that never
    // emits a newline would grow the accumulating buffer until the HOST process OOMs, not just this session.
    // 16 MiB is a generous multiple of any legitimate NDJSON line (a session/update payload runs a few KB).
    // Counted in chars rather than exact UTF-8 bytes — a reasonable proxy, since kimi's wire traffic is
    // near-ASCII JSON — not a byte-perfect budget.
    internal const int MaxLineLengthChars = 16 * 1024 * 1024;

    private Process? _process;
    private bool _started;
    private bool _disposed;
    private int _droppedOversizedLineCount;

    public int? ProcessId => !_disposed && _started && _process is { HasExited: false } process ? process.Id : null;

    // P1-9a: how many stdout/stderr lines were dropped for exceeding MaxLineLengthChars — a caller can inspect
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
            // kimi acp speaks UTF-8 on all three streams; without pinning the redirected streams .NET
            // decodes with the OS default code page (ANSI/OEM on Windows) and mangles non-ASCII output —
            // the same mojibake bug ClaudeCliProcess/CodexProcessCliSubprocess pin against.
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

    // Test seam for P1-9a (InternalsVisibleTo): exercises the capped line reader against any StreamReader,
    // without spawning a real process.
    internal IAsyncEnumerable<string> ReadLinesAsyncForTests(StreamReader reader, CancellationToken cancellationToken = default) =>
        _ReadLinesAsync(reader, cancellationToken);

    // P1-9a: replaces StreamReader.ReadLineAsync (no length limit of its own) with a capped accumulator. A line
    // that grows past MaxLineLengthChars is dropped, and the rest of that same line is skipped up to the next
    // '\n' rather than buffered in full — so the stream re-synchronises on the next NDJSON line (NDJSON is
    // line-based) instead of desyncing or growing without bound.
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
                            // Drop what has been buffered rather than let a line with no newline in sight grow
                            // forever — that is the OOM vector this fix closes.
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
                    // Kill the entire tree, not just the direct child — matches ClaudeCliProcess/
                    // CodexProcessCliSubprocess's discipline so a stuck kimi never leaves zombie grandchildren.
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
