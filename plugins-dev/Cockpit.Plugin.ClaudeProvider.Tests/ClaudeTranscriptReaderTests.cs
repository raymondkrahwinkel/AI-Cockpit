using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeTranscriptReader"/> (#35b/#39, weg A): locates the session's live JSONL transcript as the
/// new <c>configDir/projects/*/*.jsonl</c> file that appears after launch (not matched by a forced session id —
/// undocumented for interactive sessions), waiting for it if the launch has not written it yet, tails it from
/// its current end so history is never replayed, and buffers a partial line across polls so a write caught
/// mid-line never surfaces as a corrupt/truncated read. Ported from the host's former in-tree reader test; the
/// only difference is the reader is keyed by the plugin's own config JSON rather than a host-supplied path.
/// </summary>
public class ClaudeTranscriptReaderTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-transcript-reader-tests-").FullName;

    // The reader resolves its state directory from the plugin's opaque config JSON, so pin the temp dir there.
    private string ConfigJson => JsonSerializer.Serialize(new ClaudeProviderConfig(ConfigDir: _configDir), ClaudeProviderConfig.JsonOptions);

    // No transcript from a prior session exists, so the one the test writes is always the "new" one.
    private static readonly IReadOnlySet<string> NoBaseline = new HashSet<string>();

    [Fact]
    public async Task ReadAssistantTextAsync_IgnoresLinesWrittenBeforeTailingStarted()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();
        await File.WriteAllTextAsync(transcriptPath, _AssistantLine("Old text, from before the tail started.") + "\n");

        var firstLine = await _ConsumeOneLineAsync(
            transcriptPath, appendAfterStarting: [_AssistantLine("New text, written after the tail started.") + "\n"]);

        firstLine.Should().Be("New text, written after the tail started.");
    }

    [Fact]
    public async Task ReadAssistantTextAsync_SkipsNonAssistantLinesAndToolUseOnlyTurns()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();

        var firstLine = await _ConsumeOneLineAsync(transcriptPath, appendAfterStarting:
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
        var transcriptPath = _CreateEmptyTranscriptFile();
        var fullLine = _AssistantLine("Split across two separate writes.");
        var splitPoint = fullLine.Length / 2;

        var firstLine = await _ConsumeOneLineAsync(
            transcriptPath,
            appendAfterStarting: [fullLine[..splitPoint]],
            thenDelay: TimeSpan.FromMilliseconds(400),
            appendAfterDelay: [fullLine[splitPoint..] + "\n"]);

        firstLine.Should().Be("Split across two separate writes.");
    }

    [Fact]
    public async Task ReadAssistantTextAsync_WhenTheTranscriptDoesNotExistYet_WaitsForItThenTailsIt()
    {
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var text in reader.ReadAssistantTextAsync(ConfigJson, NoBaseline, cts.Token))
            {
                received.Add(text);
                break;
            }
        });

        // Nothing under projects/ yet — the reader must keep polling instead of giving up.
        await Task.Delay(400);
        Directory.CreateDirectory(projectDir);
        var transcriptPath = Path.Combine(projectDir, $"{Guid.NewGuid()}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, string.Empty);

        // Let the reader notice the (empty) file and seek to its end before anything is written to it.
        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, _AssistantLine("Appeared after the launch.") + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().ContainSingle().Which.Should().Be("Appeared after the launch.");
    }

    [Fact]
    public async Task ReadActivityAsync_YieldsEveryAppendedRawLine_NotJustAssistantText()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.RawLine is not { } line)
                {
                    continue;
                }

                received.Add(line);
                if (received.Count == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, """{"type":"user","message":{"content":[]}}""" + "\n");
        await File.AppendAllTextAsync(transcriptPath, _AssistantLine("hi") + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        received.Should().HaveCount(2);
        received[0].Should().Contain("\"type\":\"user\"");
        received[1].Should().Contain("\"type\":\"assistant\"");
    }

    [Fact]
    public async Task ReadActivityAsync_WhileASubAgentIsWriting_YieldsBackgroundBusy()
    {
        // The bug this guards: a Claude sub-agent (Task tool) records to a sibling <id>/subagents/ folder, not the
        // main transcript, so the main tail goes quiet and the status used to time out to Done. The reader now sees
        // that background activity and reports it.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var subDir = Path.Combine(
            Path.GetDirectoryName(transcriptPath)!, Path.GetFileNameWithoutExtension(transcriptPath), "subagents");

        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sawBackground = false;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Activity == PluginSessionActivity.BackgroundBusy)
                {
                    sawBackground = true;
                    break;
                }
            }
        });

        await Task.Delay(500);
        // The main agent spawns a sub-agent (a tool_use turn) → the reader is now mid-turn.
        await File.AppendAllTextAsync(
            transcriptPath, """{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}""" + "\n");
        // The sub-agent writes to its own folder while the main transcript stays quiet.
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "agent-abc.jsonl"), "{}\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        sawBackground.Should().BeTrue("a sub-agent writing while the main transcript is quiet is background work");
    }

    [Fact]
    public async Task ReadActivityAsync_WhenAnAgentIsStoppedByUser_LeavesBackgroundBusy_EvenWithAFreshTranscript()
    {
        // The bug this guards: stopping a background agent writes a final line to its transcript, so a pure
        // folder-mtime check keeps reading "still running". The agent's meta.json marks it stoppedByUser at once,
        // so the dot moves off background (here: to Done, since the main turn had completed).
        var transcriptPath = _CreateEmptyTranscriptFile();
        var subDir = Path.Combine(
            Path.GetDirectoryName(transcriptPath)!, Path.GetFileNameWithoutExtension(transcriptPath), "subagents");
        Directory.CreateDirectory(subDir);
        var agentTranscript = Path.Combine(subDir, "agent-abc.jsonl");
        var agentMeta = Path.Combine(subDir, "agent-abc.meta.json");

        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sawBackground = false;
        PluginSessionActivity? afterStop = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Activity == PluginSessionActivity.BackgroundBusy)
                {
                    sawBackground = true;
                }
                else if (sawBackground && reading.Activity != PluginSessionActivity.None)
                {
                    afterStop = reading.Activity;
                    break;
                }
            }
        });

        await Task.Delay(500);
        // A running agent (no stop marker yet) while the main turn completes → downgraded to background work.
        await File.WriteAllTextAsync(agentMeta, """{"agentType":"general-purpose","stoppedByUser":false}""");
        await File.WriteAllTextAsync(agentTranscript, "{}\n");
        await File.AppendAllTextAsync(
            transcriptPath, """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn"}}""" + "\n");

        await _WaitUntilAsync(() => sawBackground);
        // The user stops the agent — its transcript gets one last write (fresh mtime), but the metadata says stopped.
        await File.WriteAllTextAsync(agentTranscript, "{}\n{}\n");
        await File.WriteAllTextAsync(agentMeta, """{"agentType":"general-purpose","stoppedByUser":true}""");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        afterStop.Should().Be(PluginSessionActivity.TurnComplete, "a user-stopped agent must free the dot despite a fresh transcript file");
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Drives one <see cref="ClaudeTranscriptReader.ReadAssistantTextAsync"/> consumption in the background (the
    /// natural <c>await foreach</c> shape production code uses), appends the given lines to the transcript once
    /// it is underway, and returns the first assistant text the reader yields.
    /// </summary>
    private async Task<string> _ConsumeOneLineAsync(
        string transcriptPath,
        IReadOnlyList<string> appendAfterStarting,
        TimeSpan? thenDelay = null,
        IReadOnlyList<string>? appendAfterDelay = null)
    {
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var text in reader.ReadAssistantTextAsync(ConfigJson, NoBaseline, cts.Token))
            {
                received.Add(text);
                break;
            }
        });

        await Task.Delay(500);
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

    private string _CreateEmptyTranscriptFile()
    {
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        Directory.CreateDirectory(projectDir);
        var transcriptPath = Path.Combine(projectDir, $"{Guid.NewGuid()}.jsonl");
        File.WriteAllText(transcriptPath, string.Empty);
        return transcriptPath;
    }

    private static string _AssistantLine(string text) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}}}";

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
