using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexTranscriptReader"/> (AC-171): before this reader existed, <c>CliAgentProviderPlugin</c>
/// registered no <c>CreateTranscriptReader</c> for the Codex TTY provider, so the host had no way to tail a
/// Codex session's rollout file — the status dot was set to Idle once at launch and never moved again, no
/// matter how much the session actually did. Locates the session's live rollout file as the new
/// <c>configDir/sessions/yyyy/MM/dd/rollout-*.jsonl</c> that appears after launch, waits for it if the launch
/// has not written it yet, tails it from its current end so history is never replayed, and buffers a partial
/// line across polls. Fixture lines below are taken verbatim from real <c>codex-cli 0.144.4</c> rollout files.
/// </summary>
public class CodexTranscriptReaderTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-codex-transcript-reader-tests-").FullName;

    // The reader resolves its state directory from the plugin's opaque config JSON, so pin the temp dir there.
    private string ConfigJson => JsonSerializer.Serialize(new CliAgentConfig(ConfigDir: _configDir), CliAgentConfig.JsonOptions);

    // No transcript from a prior session exists, so the one the test writes is always the "new" one.
    private static readonly IReadOnlySet<string> NoBaseline = new HashSet<string>();

    [Theory]
    [InlineData("""{"type":"event_msg","payload":{"type":"task_started"}}""", PluginSessionActivity.Busy)]
    [InlineData("""{"type":"event_msg","payload":{"type":"task_complete","turn_id":"x"}}""", PluginSessionActivity.TurnComplete)]
    [InlineData("""{"type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""", PluginSessionActivity.None)]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","info":{}}}""", PluginSessionActivity.None)]
    [InlineData("""{"type":"event_msg","payload":{"type":"user_message","message":"hi"}}""", PluginSessionActivity.None)]
    [InlineData("""{"type":"response_item","payload":{"type":"function_call"}}""", PluginSessionActivity.None)]
    [InlineData("""{"type":"session_meta","payload":{}}""", PluginSessionActivity.None)]
    [InlineData("""{"type":"event_msg"}""", PluginSessionActivity.None)]
    [InlineData("not json", PluginSessionActivity.None)]
    [InlineData("", PluginSessionActivity.None)]
    public void ClassifyLine_ReadsTheEventMsgPayloadType(string line, PluginSessionActivity expected) =>
        Assert.Equal(expected, CodexTranscriptReader.ClassifyLine(line));

    [Fact]
    public void TryExtractAssistantText_ReadsAnAgentMessage()
    {
        var extracted = CodexTranscriptReader.TryExtractAssistantText(
            """{"type":"event_msg","payload":{"type":"agent_message","message":"Afgesloten.","phase":"final_answer"}}""",
            out var text);

        Assert.True(extracted);
        Assert.Equal("Afgesloten.", text);
    }

    [Theory]
    [InlineData("""{"type":"event_msg","payload":{"type":"task_started"}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"agent_message","message":""}}""")]
    [InlineData("""{"type":"response_item","payload":{"type":"message"}}""")]
    public void TryExtractAssistantText_IgnoresNonAgentMessageOrEmptyText(string line) =>
        Assert.False(CodexTranscriptReader.TryExtractAssistantText(line, out _));

    [Fact]
    public async Task ReadAssistantTextAsync_ReadsContentAlreadyInTheFileWhenTailingStarts()
    {
        // Unlike Claude (whose transcript is created empty at launch, so anything already in it predates the
        // session and is safe to skip), a codex rollout can already hold this session's own opening content by
        // the time the reader notices it — see ReadActivityAsync_WhenTheFileAppearsAlreadyContainingTaskStarted
        // for why skipping to the end drops that. This is the ReadAssistantTextAsync-facing side of the same fix.
        var transcriptPath = _CreateEmptyTranscriptFile();
        await File.WriteAllTextAsync(transcriptPath, _AgentMessageLine("Already there when the tail started.") + "\n");

        var reader = new CodexTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        await foreach (var text in reader.ReadAssistantTextAsync(ConfigJson, NoBaseline, cts.Token))
        {
            received.Add(text);
            break;
        }

        Assert.Equal("Already there when the tail started.", Assert.Single(received));
    }

    [Fact]
    public async Task ReadAssistantTextAsync_SkipsNonAgentMessageLines()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();

        var firstLine = await _ConsumeOneLineAsync(transcriptPath, appendAfterStarting:
        [
            """{"type":"event_msg","payload":{"type":"task_started"}}""" + "\n" +
            """{"type":"event_msg","payload":{"type":"user_message","message":"ignored"}}""" + "\n" +
            """{"type":"response_item","payload":{"type":"reasoning"}}""" + "\n" +
            _AgentMessageLine("The only line worth reading.") + "\n",
        ]);

        Assert.Equal("The only line worth reading.", firstLine);
    }

    [Fact]
    public async Task ReadAssistantTextAsync_BuffersAPartialLine_UntilItsNewlineArrivesInALaterWrite()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();
        var fullLine = _AgentMessageLine("Split across two separate writes.");
        var splitPoint = fullLine.Length / 2;

        var firstLine = await _ConsumeOneLineAsync(
            transcriptPath,
            appendAfterStarting: [fullLine[..splitPoint]],
            thenDelay: TimeSpan.FromMilliseconds(400),
            appendAfterDelay: [fullLine[splitPoint..] + "\n"]);

        Assert.Equal("Split across two separate writes.", firstLine);
    }

    [Fact]
    public async Task ReadAssistantTextAsync_WhenTheTranscriptDoesNotExistYet_WaitsForItThenTailsIt()
    {
        var sessionDayDir = Path.Combine(_configDir, "sessions", "2026", "07", "30");
        var reader = new CodexTranscriptReader();
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

        // Nothing under sessions/ yet — the reader must keep polling instead of giving up.
        await Task.Delay(400);
        Directory.CreateDirectory(sessionDayDir);
        var transcriptPath = Path.Combine(sessionDayDir, $"rollout-2026-07-30T12-00-00-{Guid.NewGuid()}.jsonl");
        await File.WriteAllTextAsync(transcriptPath, string.Empty);

        // Let the reader notice the (empty) file and seek to its end before anything is written to it.
        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, _AgentMessageLine("Appeared after the launch.") + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Appeared after the launch.", Assert.Single(received));
    }

    [Fact]
    public async Task ReadActivityAsync_YieldsBusyOnTaskStarted_ThenTurnCompleteOnTaskComplete()
    {
        // The bug this guards (AC-171): with no reader wired at all, the host never learns of either event, so
        // the TTY status dot is set to Idle once at launch and stays there for the rest of the session.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new CodexTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var activities = new List<PluginSessionActivity>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Activity == PluginSessionActivity.None)
                {
                    continue;
                }

                activities.Add(reading.Activity);
                if (activities.Count == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, """{"type":"event_msg","payload":{"type":"task_started"}}""" + "\n");
        await File.AppendAllTextAsync(transcriptPath, """{"type":"event_msg","payload":{"type":"user_message","message":"hi"}}""" + "\n");
        await File.AppendAllTextAsync(transcriptPath, """{"type":"event_msg","payload":{"type":"task_complete","turn_id":"x"}}""" + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([PluginSessionActivity.Busy, PluginSessionActivity.TurnComplete], activities);
    }

    [Fact]
    public async Task ReadActivityAsync_WhenTheFileAppearsAlreadyContainingTaskStarted_StillYieldsBusy()
    {
        // The bug this guards (found by adversarial review): unlike Claude's transcript, which is created empty
        // at launch, codex does not create the rollout file until the operator's first turn actually starts —
        // by the time the poll in _WaitForNewTranscriptAsync notices the file, it can already contain both
        // "session_meta" and the first turn's "task_started". Seeking to the end (as the first cut of this
        // reader did) silently drops that opening Busy signal for exactly the common single-turn session —
        // reproducing AC-171's own symptom instead of fixing it. This writes the file in one shot, as codex
        // does, rather than empty-then-append like the other tests here.
        var sessionDayDir = Path.Combine(_configDir, "sessions", "2026", "07", "30");
        Directory.CreateDirectory(sessionDayDir);
        var transcriptPath = Path.Combine(sessionDayDir, $"rollout-2026-07-30T12-00-00-{Guid.NewGuid()}.jsonl");

        var reader = new CodexTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        PluginSessionActivity? firstNonNone = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Activity == PluginSessionActivity.None)
                {
                    continue;
                }

                firstNonNone = reading.Activity;
                break;
            }
        });

        // The reader is polling for a new file; only now does it appear — pre-populated, as codex writes it.
        await Task.Delay(400);
        await File.WriteAllTextAsync(
            transcriptPath,
            """{"type":"session_meta","payload":{}}""" + "\n" +
            """{"type":"event_msg","payload":{"type":"task_started"}}""" + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PluginSessionActivity.Busy, firstNonNone);
    }

    [Fact]
    public void SnapshotTranscripts_ReturnsExistingRolloutsAcrossTheDateTree_NotJustOneDay()
    {
        var day1 = Path.Combine(_configDir, "sessions", "2026", "07", "15");
        var day2 = Path.Combine(_configDir, "sessions", "2026", "07", "30");
        Directory.CreateDirectory(day1);
        Directory.CreateDirectory(day2);
        var older = Path.Combine(day1, $"rollout-2026-07-15T00-00-00-{Guid.NewGuid()}.jsonl");
        var newer = Path.Combine(day2, $"rollout-2026-07-30T00-00-00-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(older, string.Empty);
        File.WriteAllText(newer, string.Empty);

        var reader = new CodexTranscriptReader();
        var snapshot = reader.SnapshotTranscripts(ConfigJson);

        Assert.Contains(older, snapshot);
        Assert.Contains(newer, snapshot);
    }

    [Fact]
    public async Task SnapshotTranscripts_ExcludesASessionThatAppearsAfterTheSnapshot_SoItReadsAsNew()
    {
        // The baseline is what lets the reader tell "this session's own new file" apart from an older one still
        // sitting on disk — a gap here would mean the reader could tail a stale, already-finished transcript.
        var reader = new CodexTranscriptReader();
        var baseline = reader.SnapshotTranscripts(ConfigJson);
        Assert.Empty(baseline);

        var transcriptPath = _CreateEmptyTranscriptFile();
        var consumeTask = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var text in reader.ReadAssistantTextAsync(ConfigJson, baseline, cts.Token))
            {
                return text;
            }

            return null;
        });

        await Task.Delay(400);
        await File.AppendAllTextAsync(transcriptPath, _AgentMessageLine("new session's own text") + "\n");

        var result = await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("new session's own text", result);
    }

    [Fact]
    public async Task ReadActivityAsync_WithNoConfigDirPinned_FallsBackToTheCodexHomeEnvironmentVariable()
    {
        // _ResolveStateDirectory's fallback chain (ConfigDir -> CODEX_HOME -> ~/.codex) only ever exercises the
        // pinned-ConfigDir branch in every other test here; this is the only one that leaves ConfigDir unset and
        // proves the CODEX_HOME env var is actually read instead.
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", _configDir);
        try
        {
            var blankConfigJson = JsonSerializer.Serialize(new CliAgentConfig(), CliAgentConfig.JsonOptions);
            var transcriptPath = _CreateEmptyTranscriptFile();
            var reader = new CodexTranscriptReader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = new List<string>();
            var consumeTask = Task.Run(async () =>
            {
                await foreach (var text in reader.ReadAssistantTextAsync(blankConfigJson, NoBaseline, cts.Token))
                {
                    received.Add(text);
                    break;
                }
            });

            await Task.Delay(500);
            await File.AppendAllTextAsync(transcriptPath, _AgentMessageLine("via CODEX_HOME") + "\n");

            await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("via CODEX_HOME", Assert.Single(received));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }

    [Fact]
    public async Task ReadAssistantTextAsync_SkipsCommentaryPhase_ButReadsFinalAnswer()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();

        var firstLine = await _ConsumeOneLineAsync(transcriptPath, appendAfterStarting:
        [
            """{"type":"event_msg","payload":{"type":"agent_message","phase":"commentary","message":"still working on it"}}""" + "\n" +
            """{"type":"event_msg","payload":{"type":"agent_message","phase":"final_answer","message":"Here is the answer."}}""" + "\n",
        ]);

        Assert.Equal("Here is the answer.", firstLine);
    }

    [Fact]
    public async Task ReadActivityAsync_YieldsEveryAppendedRawLine_NotJustClassifiedOnes()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new CodexTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                received.Add(reading.RawLine!);
                if (received.Count == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, """{"type":"event_msg","payload":{"type":"token_count","info":{}}}""" + "\n");
        await File.AppendAllTextAsync(transcriptPath, _AgentMessageLine("hi") + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, received.Count);
        Assert.Contains("\"token_count\"", received[0]);
        Assert.Contains("\"agent_message\"", received[1]);
    }

    private async Task<string> _ConsumeOneLineAsync(
        string transcriptPath,
        IReadOnlyList<string> appendAfterStarting,
        TimeSpan? thenDelay = null,
        IReadOnlyList<string>? appendAfterDelay = null)
    {
        var reader = new CodexTranscriptReader();
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
        return Assert.Single(received);
    }

    private string _CreateEmptyTranscriptFile()
    {
        var sessionDayDir = Path.Combine(_configDir, "sessions", "2026", "07", "30");
        Directory.CreateDirectory(sessionDayDir);
        var transcriptPath = Path.Combine(sessionDayDir, $"rollout-2026-07-30T12-00-00-{Guid.NewGuid()}.jsonl");
        File.WriteAllText(transcriptPath, string.Empty);
        return transcriptPath;
    }

    private static string _AgentMessageLine(string text) =>
        "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"" + text + "\"}}";

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
