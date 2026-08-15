using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// AC-783: a copy of Kimi's own FakeCliSubprocess — records every Start/WriteLineAsync call and lets a
// test push stdout/stderr lines on demand, standing in for a real spawned opencode acp process.
internal sealed class FakeCliSubprocess : ICliSubprocess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr;

    // `stderrCapacity`: zero (default) is unbounded; a positive value makes stderr bounded, used by the
    // stderr-deadlock test to prove a concurrent drain task is actually required.
    public FakeCliSubprocess(int stderrCapacity = 0)
    {
        _stderr = stderrCapacity > 0 ? Channel.CreateBounded<string>(stderrCapacity) : Channel.CreateUnbounded<string>();
    }

    public string? ExecutablePath { get; private set; }

    public IReadOnlyList<string>? Arguments { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; private set; }

    // A plain List<string> would throw an intermittent InvalidOperationException ("Collection was modified")
    // once a background write races a test's own LINQ read over it. ConcurrentQueue's enumerator is
    // documented safe to use while another thread concurrently enqueues.
    public ConcurrentQueue<string> WrittenLines { get; } = new();

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
        WrittenLines.Enqueue(line);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default) =>
        _stdout.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default) =>
        _stderr.Reader.ReadAllAsync(cancellationToken);

    public Task PushStdoutAsync(string line) => _stdout.Writer.WriteAsync(line).AsTask();

    public Task PushStderrAsync(string line) => _stderr.Writer.WriteAsync(line).AsTask();

    // Simulates the child process exiting cleanly — both pipes close together, as they would for a real process.
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
