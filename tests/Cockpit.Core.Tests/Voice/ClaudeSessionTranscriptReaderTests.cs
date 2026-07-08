using Cockpit.Infrastructure.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="ClaudeSessionTranscriptReader"/> (#35b): finds the session's live JSONL transcript under
/// <c>configDir/projects/*/&lt;session-id&gt;.jsonl</c> (waiting for it to appear if the launch hasn't
/// written it yet), tails it from its current end so history is never replayed, and buffers a partial
/// line across polls so a write caught mid-line never surfaces as a corrupt/truncated read.
/// </summary>
public class ClaudeSessionTranscriptReaderTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-transcript-reader-tests-").FullName;

    [Fact]
    public async Task ReadAssistantTextAsync_IgnoresLinesWrittenBeforeTailingStarted()
    {
        var sessionId = Guid.NewGuid();
        var transcriptPath = _CreateEmptyTranscriptFile(sessionId);
        await File.WriteAllTextAsync(transcriptPath, _AssistantLine("Old text, from before the tail started.") + "\n");

        var firstLine = await _ConsumeOneLineAsync(
            sessionId, appendAfterStarting: [_AssistantLine("New text, written after the tail started.") + "\n"]);

        firstLine.Should().Be("New text, written after the tail started.");
    }

    [Fact]
    public async Task ReadAssistantTextAsync_SkipsNonAssistantLinesAndToolUseOnlyTurns()
    {
        var sessionId = Guid.NewGuid();
        _CreateEmptyTranscriptFile(sessionId);

        var firstLine = await _ConsumeOneLineAsync(sessionId, appendAfterStarting:
        [
            """{"type":"user","message":{"content":[{"type":"text","text":"ignored"}]}}""" + "\n" +
            """{"type":"system","subtype":"init"}""" + "\n" +
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{}}]}}""" + "\n" +
            _AssistantLine("The only line worth reading.") + "\n",
        ]);

        firstLine.Should().Be("The only line worth reading.");
    }

    [Fact]
    public async Task ReadAssistantTextAsync_BuffersAPartialLine_UntilItsNewlineArrivesInALaterWrite()
    {
        var sessionId = Guid.NewGuid();
        _CreateEmptyTranscriptFile(sessionId);
        var fullLine = _AssistantLine("Split across two separate writes.");
        var splitPoint = fullLine.Length / 2;

        var firstLine = await _ConsumeOneLineAsync(
            sessionId,
            appendAfterStarting: [fullLine[..splitPoint]],
            thenDelay: TimeSpan.FromMilliseconds(400),
            appendAfterDelay: [fullLine[splitPoint..] + "\n"]);

        firstLine.Should().Be("Split across two separate writes.");
    }

    [Fact]
    public async Task ReadAssistantTextAsync_WhenTheTranscriptDoesNotExistYet_WaitsForItThenTailsIt()
    {
        var sessionId = Guid.NewGuid();
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        var reader = new ClaudeSessionTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var text in reader.ReadAssistantTextAsync(_configDir, sessionId, cts.Token))
            {
                received.Add(text);
                break;
            }
        });

        // Nothing under projects/ yet — the reader must keep polling instead of giving up.
        await Task.Delay(400);
        Directory.CreateDirectory(projectDir);
        var transcriptPath = Path.Combine(projectDir, $"{sessionId}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, string.Empty);

        // Let the reader notice the (empty) file and seek to its end before anything is written to it.
        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, _AssistantLine("Appeared after the launch.") + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().ContainSingle().Which.Should().Be("Appeared after the launch.");
    }

    /// <summary>
    /// Drives one <see cref="ClaudeSessionTranscriptReader.ReadAssistantTextAsync"/> consumption in the
    /// background (the natural <c>await foreach</c> shape production code uses — <c>GetAsyncEnumerator</c>
    /// plus a second, separately supplied cancellation token is not how <c>[EnumeratorCancellation]</c> is
    /// meant to be driven, and doing so left the reader's own token forever un-cancelled in an earlier
    /// version of this test), appends the given lines to the transcript once it is underway, and returns
    /// the first assistant text the reader yields.
    /// </summary>
    private async Task<string> _ConsumeOneLineAsync(
        Guid sessionId,
        IReadOnlyList<string> appendAfterStarting,
        Func<Task>? beforeAppending = null,
        TimeSpan? thenDelay = null,
        IReadOnlyList<string>? appendAfterDelay = null)
    {
        var transcriptPath = Path.Combine(_configDir, "projects", "some-cwd-hash", $"{sessionId}.jsonl");
        var reader = new ClaudeSessionTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var text in reader.ReadAssistantTextAsync(_configDir, sessionId, cts.Token))
            {
                received.Add(text);
                break;
            }
        });

        await (beforeAppending?.Invoke() ?? Task.Delay(500));
        foreach (var line in appendAfterStarting)
        {
            await File.AppendAllTextAsync(transcriptPath, line);
        }

        if (thenDelay is { } delay)
        {
            await Task.Delay(delay);
            foreach (var line in appendAfterDelay ?? [])
            {
                await File.AppendAllTextAsync(transcriptPath, line);
            }
        }

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        return received.Should().ContainSingle().Subject;
    }

    private string _CreateEmptyTranscriptFile(Guid sessionId)
    {
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        Directory.CreateDirectory(projectDir);
        var transcriptPath = Path.Combine(projectDir, $"{sessionId}.jsonl");
        File.WriteAllText(transcriptPath, string.Empty);
        return transcriptPath;
    }

    private static string _AssistantLine(string text) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
