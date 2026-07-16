using System.Threading.Channels;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// A hand-written <see cref="ICliSubprocess"/> test double (#45 fase B1): records every <see cref="Start"/>/
/// <see cref="WriteLineAsync"/> call it receives and lets a test push stdout/stderr lines on demand, standing
/// in for a real spawned <c>codex</c> process in <see cref="CliSubprocessPluginSessionDriverTests"/>.
/// </summary>
internal sealed class FakeCliSubprocess : ICliSubprocess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr;

    /// <param name="stderrCapacity">
    /// Zero (default) is an unbounded stderr channel. A positive capacity makes stderr a *bounded* channel —
    /// used by the stderr-deadlock test: without a concurrent drain task actually reading it, a write past
    /// capacity blocks forever, proving the driver really does drain stderr alongside stdout rather than
    /// only after stdout completes.
    /// </param>
    public FakeCliSubprocess(int stderrCapacity = 0)
    {
        _stderr = stderrCapacity > 0 ? Channel.CreateBounded<string>(stderrCapacity) : Channel.CreateUnbounded<string>();
    }

    public string? ExecutablePath { get; private set; }

    public IReadOnlyList<string>? Arguments { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; private set; }

    public List<string> WrittenLines { get; } = [];

    public bool Disposed { get; private set; }

    public int? ProcessId { get; set; } = 4242;

    public bool HasExited { get; private set; }

    public int? ExitCode { get; set; }

    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        ExecutablePath = executablePath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        EnvironmentVariables = environmentVariables;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        WrittenLines.Add(line);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default) =>
        _stdout.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default) =>
        _stderr.Reader.ReadAllAsync(cancellationToken);

    public Task PushStdoutAsync(string line) => _stdout.Writer.WriteAsync(line).AsTask();

    public Task PushStderrAsync(string line) => _stderr.Writer.WriteAsync(line).AsTask();

    /// <summary>Simulates the child process exiting cleanly — both pipes close together, as they would for a real process.</summary>
    public void CompleteStdout(int exitCode = 0)
    {
        ExitCode = exitCode;
        HasExited = true;
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        if (Disposed)
        {
            return ValueTask.CompletedTask;
        }

        Disposed = true;
        HasExited = true;
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
