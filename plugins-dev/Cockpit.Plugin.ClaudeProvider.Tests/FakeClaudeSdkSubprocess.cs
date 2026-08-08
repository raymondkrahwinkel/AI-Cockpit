using System.Threading.Channels;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// A hand-written `IClaudeSdkSubprocess` test double (Fase 4): records every `Start`/
// `WriteLineAsync` call and lets a test push stdout/stderr lines on demand, standing in for a real spawned
// `claude` process in `ClaudeSdkSessionDriverTests` — the plugin has no logged-in CLI to run against.
internal sealed class FakeClaudeSdkSubprocess : IClaudeSdkSubprocess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();

    private readonly List<string> _writtenLines = [];

    // A snapshot, because the driver's usage poll writes from its own task while a test reads: the poll is
    // fire-and-forget off the stdout pump, so a plain List would be enumerated mid-Add.
    public IReadOnlyList<string> WrittenLines
    {
        get
        {
            lock (_writtenLines)
            {
                return [.. _writtenLines];
            }
        }
    }

    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; private set; }

    // The argument list the driver actually spawned with — what AC-378's --strict-mcp-config tests assert on.
    public IReadOnlyList<string>? Arguments { get; private set; }

    public bool Disposed { get; private set; }

    public int? ProcessId { get; set; } = 4242;

    public bool HasExited { get; private set; }

    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
        Arguments = arguments;
        EnvironmentVariables = environmentVariables;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        lock (_writtenLines)
        {
            _writtenLines.Add(line);
        }

        return Task.CompletedTask;
    }

    public IAsyncEnumerable<string> ReadStdoutLinesAsync(CancellationToken cancellationToken = default) =>
        _stdout.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<string> ReadStderrLinesAsync(CancellationToken cancellationToken = default) =>
        _stderr.Reader.ReadAllAsync(cancellationToken);

    public Task PushStdoutAsync(string line) => _stdout.Writer.WriteAsync(line).AsTask();

    public void CompleteStdout()
    {
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
