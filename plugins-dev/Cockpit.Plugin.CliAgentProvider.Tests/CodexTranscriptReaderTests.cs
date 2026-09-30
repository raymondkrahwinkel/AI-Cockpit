using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// AC-171: without a transcript reader wired up, the host had no way to tail a Codex session's
// rollout file, so the status dot was set to Idle once at launch and never moved again. This reader
// locates `configDir/sessions/yyyy/MM/dd/rollout-*.jsonl`, waits for it, and tails from its current end.
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
