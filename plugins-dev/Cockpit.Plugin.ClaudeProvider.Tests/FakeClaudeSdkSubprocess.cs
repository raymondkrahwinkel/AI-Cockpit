using System.Threading.Channels;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// A hand-written <see cref="IClaudeSdkSubprocess"/> test double (Fase 4): records every <see cref="Start"/>/
/// <see cref="WriteLineAsync"/> call and lets a test push stdout/stderr lines on demand, standing in for a real spawned
/// <c>claude</c> process in <see cref="ClaudeSdkSessionDriverTests"/> — the plugin has no logged-in CLI to run against.
/// </summary>
internal sealed class FakeClaudeSdkSubprocess : IClaudeSdkSubprocess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();

    public List<string> WrittenLines { get; } = [];

    public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; private set; }

    public bool Disposed { get; private set; }

    public int? ProcessId { get; set; } = 4242;

    public bool HasExited { get; private set; }

    public void Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?> environmentVariables)
    {
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
