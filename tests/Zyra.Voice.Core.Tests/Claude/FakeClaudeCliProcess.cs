using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Zyra.Voice.Core.Profiles;
using Zyra.Voice.Infrastructure.Claude;

namespace Zyra.Voice.Core.Tests.Claude;

/// <summary>
/// In-memory <see cref="IClaudeCliProcess"/> test double: lets tests push canned stdout
/// lines and inspect what was written to "stdin", without spawning a real process.
/// </summary>
internal sealed class FakeClaudeCliProcess : IClaudeCliProcess
{
    private readonly Channel<string> _outputLines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    public List<string> WrittenLines { get; } = [];

    public bool Started { get; private set; }

    public bool HasExited { get; private set; }

    public ClaudeProfile? StartedWithProfile { get; private set; }

    public void Enqueue(string line) => _outputLines.Writer.TryWrite(line);

    public void CompleteOutput() => _outputLines.Writer.TryComplete();

    public void Start(ClaudeProfile? profile = null)
    {
        Started = true;
        StartedWithProfile = profile;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        WrittenLines.Add(line);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var line in _outputLines.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }
    }

    public ValueTask DisposeAsync()
    {
        HasExited = true;
        _outputLines.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
