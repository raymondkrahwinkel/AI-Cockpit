using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

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

        Assert.Equal("New text, written after the tail started.", firstLine);
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

        Assert.Equal("The only line worth reading.", firstLine);
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

        Assert.Equal("Split across two separate writes.", firstLine);
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
        Assert.Equal("Appeared after the launch.", Assert.Single(received));
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
        Assert.Equal(2, System.Linq.Enumerable.Count(received));
        Assert.Contains("\"type\":\"user\"", received[0]);
        Assert.Contains("\"type\":\"assistant\"", received[1]);
    }

    // AC-276 replaced the sub-agent mtime window with the count the CLI states on each turn_duration line. The two
    // tests these replace drove <id>/subagents/agent-*.jsonl file timestamps; that directory is no longer read.
    private const string EndTurnLine = """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn"}}""";

    private static string TurnDurationLine(int? pendingSubAgents) => pendingSubAgents is { } count
        ? $$"""{"type":"system","subtype":"turn_duration","durationMs":1200,"pendingBackgroundAgentCount":{{count}}}"""
        : """{"type":"system","subtype":"turn_duration","durationMs":1200}""";

    [Fact]
    public async Task ReadActivityAsync_WhenTheTurnEndsWithSubAgentsStillPending_YieldsBackgroundBusy()
    {
        // The bug this guards: the main agent legitimately reaches end_turn while sub-agents it spawned keep
        // running, which used to drop the session straight to Done. The CLI states how many are still pending on
        // the turn_duration line that closes the turn, so that reading is what decides — not a file's timestamp.
        var transcriptPath = _CreateEmptyTranscriptFile();
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
        // The main agent's own turn ends — on its own that reads as Done ...
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");
        // ... until the turn_duration line says three sub-agents are still running.
        await File.AppendAllTextAsync(transcriptPath, TurnDurationLine(3) + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(sawBackground, "a turn that ends while sub-agents are pending is background work, not done");
    }

    [Fact]
    public async Task ReadActivityAsync_KeepsTheReportedCount_ForLaterTurnsInTheSameRun()
    {
        // Caught by mutation testing: asserting only on the turn_duration line itself passes even when the count is
        // never stored, because that line sets the activity directly. What actually has to hold is that the count
        // *persists* — the CLI states it once per turn, and every later turn ending has to keep reading as
        // background work until a turn_duration says otherwise. This drives a second end_turn with no turn_duration
        // of its own, so only the remembered count can produce the right answer.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readings = new List<PluginSessionActivity>();
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.RawLine is null || reading.Activity == PluginSessionActivity.None)
                {
                    continue;
                }

                readings.Add(reading.Activity);
                if (readings.Count == 3)
                {
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");          // 1: TurnComplete
        await File.AppendAllTextAsync(transcriptPath, TurnDurationLine(2) + "\n");  // 2: corrected to background
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");          // 3: still background

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            [PluginSessionActivity.TurnComplete, PluginSessionActivity.BackgroundBusy, PluginSessionActivity.BackgroundBusy],
            readings);
    }

    [Fact]
    public async Task ReadActivityAsync_WhenTheNextTurnReportsNoPendingSubAgents_LeavesBackgroundBusy()
    {
        // The other half: the count is restated every turn, so a session that was background work returns to its
        // own state once the CLI stops reporting pending agents. The field is absent (not zero) when none are
        // pending — measured across 232 transcripts, no turn_duration line ever carries the value 0 — so an absent
        // field has to read as "none left", not as "no information".
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sawBackground = false;
        PluginSessionActivity? afterAgentsFinished = null;
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
                    afterAgentsFinished = reading.Activity;
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");
        await File.AppendAllTextAsync(transcriptPath, TurnDurationLine(1) + "\n");
        await _WaitUntilAsync(() => sawBackground);

        // A later turn ends with nothing pending — the field is simply absent.
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");
        await File.AppendAllTextAsync(transcriptPath, TurnDurationLine(null) + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PluginSessionActivity.TurnComplete, afterAgentsFinished);
    }

    [Fact]
    public async Task ReadActivityAsync_CountsABackgroundShell_WithoutHoldingTheStatus()
    {
        // The regression this guards is the fix's own worst failure mode: a backgrounded shell can be a dev server
        // or a tail -f that never ends. It must be *counted* (so the host can withhold the "session finished"
        // notification) while the status still reaches TurnComplete — anything else pins such a session on
        // "working" for as long as the server runs, which is worse than the premature Done this ticket is about.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PluginTranscriptActivity? atTurnEnd = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Activity == PluginSessionActivity.TurnComplete)
                {
                    atTurnEnd = reading;
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(
            transcriptPath,
            """{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_shell1","name":"Bash","input":{"command":"npm run dev","run_in_background":true}}]}}"""
                + "\n");
        // The turn ends with no sub-agents pending — a never-ending shell must not stand in the way of that.
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(atTurnEnd);
        Assert.Equal(PluginSessionActivity.TurnComplete, atTurnEnd!.Activity);
        Assert.Equal(1, atTurnEnd.OutstandingShells);
    }

    [Fact]
    public async Task ReadActivityAsync_WhenAShellsNotificationArrives_StopsCountingIt()
    {
        // The shell ledger's end signal: the CLI queues a <task-notification> block naming the same tool_use id
        // that started it. Without this the count would only ever grow, and a session that had run one background
        // command would never be announced as finished again.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var sawShell = false;
        int? afterNotification = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.OutstandingShells > 0)
                {
                    sawShell = true;
                }
                else if (sawShell)
                {
                    afterNotification = reading.OutstandingShells;
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(
            transcriptPath,
            """{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_shell1","name":"Bash","input":{"command":"sleep 90","run_in_background":true}}]}}"""
                + "\n");
        await _WaitUntilAsync(() => sawShell);

        await File.AppendAllTextAsync(
            transcriptPath,
            """{"type":"queue-operation","operation":"enqueue","content":"<task-notification>\n<task-id>b5ddh99hr</task-id>\n<tool-use-id>toolu_shell1</tool-use-id>\n<status>completed</status>\n</task-notification>"}"""
                + "\n");
        await File.AppendAllTextAsync(transcriptPath, EndTurnLine + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, afterNotification);
    }

    [Fact]
    public async Task ReadActivityAsync_CarriesUsage_FromAnAssistantLine()
    {
        // AC-398: the same tail read-aloud/status already use, now also carrying the token usage an assistant
        // line reports — so the host's usage trail can be fed from the transcript tail without a second read of
        // the same file.
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        PluginTokenUsage? usage = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Usage is { } seen)
                {
                    usage = seen;
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(
            transcriptPath,
            """{"type":"assistant","message":{"role":"assistant","stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""" + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new PluginTokenUsage(10, 20, 0, 0), usage);
    }

    [Fact]
    public async Task ReadActivityAsync_RepeatedLineForTheSameApiResponse_CountsUsageAndTurnOnlyOnce()
    {
        // The bug this guards: the CLI can write more than one transcript line for the same assistant API
        // response (progressive content-block saves) — each repeat carries the identical message.id, stop_reason
        // and usage as the first. Summing every line rather than every distinct response inflated real transcripts
        // 2-2.8x (an AC-481-shaped bug: a figure that is really "per API response" treated as "per line").
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var usageReadings = new List<PluginTokenUsage>();
        var turnCompletions = 0;
        var linesSeen = 0;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.Usage is { } seen)
                {
                    usageReadings.Add(seen);
                }

                if (reading.Activity == PluginSessionActivity.TurnComplete)
                {
                    turnCompletions++;
                }

                if (reading.RawLine is not null && ++linesSeen == 2)
                {
                    break;
                }
            }
        });

        await Task.Delay(500);
        // Same message.id, same usage, same stop_reason — a thinking block then a text block of the one response.
        const string repeatedLine =
            """{"type":"assistant","message":{"id":"msg_01","role":"assistant","stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":20,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""";
        await File.AppendAllTextAsync(transcriptPath, repeatedLine + "\n" + repeatedLine + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        var singleReading = Assert.Single(usageReadings);
        Assert.Equal(new PluginTokenUsage(10, 20, 0, 0), singleReading);
        Assert.Equal(1, turnCompletions);
    }

    [Fact]
    public async Task ReadActivityAsync_NonAssistantLine_CarriesNoUsage()
    {
        var transcriptPath = _CreateEmptyTranscriptFile();
        var reader = new ClaudeTranscriptReader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        PluginTranscriptActivity? received = null;
        var consumeTask = Task.Run(async () =>
        {
            await foreach (var reading in reader.ReadActivityAsync(ConfigJson, NoBaseline, cts.Token))
            {
                if (reading.RawLine is not null)
                {
                    received = reading;
                    break;
                }
            }
        });

        await Task.Delay(500);
        await File.AppendAllTextAsync(transcriptPath, """{"type":"user","message":{"content":[]}}""" + "\n");

        await consumeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(received!.Usage);
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
        return Assert.Single(received);
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
