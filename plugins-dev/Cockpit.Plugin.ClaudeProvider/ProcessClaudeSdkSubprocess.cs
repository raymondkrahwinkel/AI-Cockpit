using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Cockpit.Plugin.ClaudeProvider;

// Real `IClaudeSdkSubprocess` backed by `System.Diagnostics.Process` — mirrors the host's `ClaudeCliProcess`
// spawn/UTF-8/dispose discipline (blueprint only, unreferenceable from this plugin). Never exercised against
// a real `claude` process here (no logged-in CLI); kept as a thin, mockable seam for the driver's unit tests.
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

    // Serialises every write to stdin: the host's user-message path and the driver's usage poll now write from
    // different threads, and `StreamWriter.WriteLineAsync` throws on a concurrent async write — or worse,
    // interleaves bytes into what is a line protocol. Here rather than per caller: all of them route through.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        var process = _RequireStartedProcess();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
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
