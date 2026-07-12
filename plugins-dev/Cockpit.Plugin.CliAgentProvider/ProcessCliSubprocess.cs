using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Real <see cref="ICliSubprocess"/> backed by <see cref="System.Diagnostics.Process"/> (#45 fase B1) —
/// mirrors <c>Cockpit.Infrastructure.Sessions.ClaudeCliProcess</c>'s spawn/UTF-8/dispose discipline exactly
/// (that class is the blueprint; this plugin cannot reference it — see the design doc §2.0), adapted for a
/// one-shot proces-per-turn spawn instead of a long-lived one.
/// </summary>
/// <remarks>
/// B2 caveat: never exercised against a real <c>codex</c> process in this environment (no logged-in Codex
/// CLI here) — kept as a thin, mockable seam so <see cref="CliSubprocessPluginSessionDriver"/>'s turn-taking
/// logic is unit tested against <c>FakeCliSubprocess</c> instead; the live end-to-end run requires Raymond's
/// environment with <c>codex</c> installed and authenticated.
/// </remarks>
internal sealed class ProcessCliSubprocess : ICliSubprocess
{
    private Process? _process;
    private bool _started;
    private bool _disposed;

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
            // Codex/Gemini CLIs speak UTF-8 on all three streams; without pinning the redirected streams
            // .NET decodes with the OS default code page (ANSI/OEM on Windows) and mangles non-ASCII output —
            // the same mojibake bug (#23) ClaudeCliProcess pins against.
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

    private static async IAsyncEnumerable<string> _ReadLinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            // Idempotent: InterruptAsync and the driver's own turn-finally can both dispose this instance
            // (e.g. an interrupt racing the turn's natural completion) — a second call must be a safe no-op,
            // not an ObjectDisposedException from touching an already-disposed Process.
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
                    // Proces-per-turn spawns many children over a session's lifetime — killing the entire
                    // tree on every dispose (not just the direct child) is what keeps that from leaving
                    // zombie grandchildren behind, matching ClaudeCliProcess.DisposeAsync's discipline.
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
