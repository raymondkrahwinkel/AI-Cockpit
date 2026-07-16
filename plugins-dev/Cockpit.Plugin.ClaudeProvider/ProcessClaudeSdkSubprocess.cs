using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// Real <see cref="IClaudeSdkSubprocess"/> backed by <see cref="System.Diagnostics.Process"/> (Fase 4) — mirrors the
/// host's <c>ClaudeCliProcess</c> spawn/UTF-8/dispose discipline (that class is the blueprint; this plugin cannot
/// reference it — weg A). Pure spawn+IO: the driver builds the arguments and environment (including dropping any
/// inherited <c>ANTHROPIC_*</c> credential and setting <c>CLAUDE_CONFIG_DIR</c>) and hands them in, exactly as the
/// CLI-agent plugin splits <c>ProcessCliSubprocess</c> from its driver.
/// </summary>
/// <remarks>
/// Never exercised against a real <c>claude</c> process in this sandbox (no logged-in CLI here) — kept as a thin,
/// mockable seam so the driver's turn-taking is unit-tested against a fake; the live end-to-end run requires Raymond's
/// logged-in environment.
/// </remarks>
internal sealed class ProcessClaudeSdkSubprocess : IClaudeSdkSubprocess
{
    private Process? _process;
    private bool _started;
    private bool _disposed;

    public int? ProcessId => !_disposed && _started && _process is { HasExited: false } process ? process.Id : null;

    public bool HasExited => _disposed || (_started && (_process?.HasExited ?? true));

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
            // claude speaks UTF-8 (→, ✅, emoji in tool output); without pinning the redirected streams to UTF-8 .NET
            // decodes them with the OS default code page and mangles them (mojibake, bug #23). BOM-less so the input
            // side stays a clean JSON byte stream.
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
        _process ?? throw new InvalidOperationException($"{nameof(ProcessClaudeSdkSubprocess)}.{nameof(Start)} must be called before I/O.");
}
