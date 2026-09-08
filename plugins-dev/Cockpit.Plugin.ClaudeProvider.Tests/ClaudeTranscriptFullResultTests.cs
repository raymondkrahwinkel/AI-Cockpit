using System.Text.Json;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// AC-1088: the host keeps only head and tail of a large tool result and reads the whole of it back out of the
// transcript the CLI writes anyway. That file is the CLI's, not Cockpit's, so this is an attempt: "gone" is an
// ordinary answer it has to give plainly rather than throw over.
public class ClaudeTranscriptFullResultTests : IDisposable
{
    private const string SessionId = "6f1c8b2a-0a11-4d1e-9d1a-77c5f0a1b2c3";

    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-full-result-tests-").FullName;

    private string ConfigJson => JsonSerializer.Serialize(new ClaudeProviderConfig(ConfigDir: _configDir), ClaudeProviderConfig.JsonOptions);

    [Fact]
    public void TheWholeResultComesBack_AndOnceTheTranscriptIsGone_TheAnswerIsSimplyNothing()
    {
        var transcript = _WriteTranscript("t1", new string('x', 5_000_000));
        var reader = new ClaudeTranscriptReader();

        var full = reader.ReadToolResult(ConfigJson, SessionId, "t1");
        Assert.NotNull(full);
        Assert.Equal(5_000_000, full!.Length);

        // The counter-proof: delete the file and ask again. No throw, no empty string that reads as a real
        // answer — null, which is what tells the row to leave the clamped result standing and say why.
        File.Delete(transcript);

        Assert.Null(reader.ReadToolResult(ConfigJson, SessionId, "t1"));
    }

    [Fact]
    public void ACallThatIsNotInTheTranscript_AndASessionWithNoTranscriptAtAll_BothComeBackEmpty()
    {
        _WriteTranscript("t1", "the one call this transcript holds");
        var reader = new ClaudeTranscriptReader();

        Assert.Null(reader.ReadToolResult(ConfigJson, SessionId, "some-other-call"));
        Assert.Null(reader.ReadToolResult(ConfigJson, "a-session-from-another-machine", "t1"));
        Assert.Null(reader.ReadToolResult(ConfigJson, sessionId: null, "t1"));
    }

    // The CLI's own shape: one JSONL line per message, a result carried as a `tool_result` block on a user message.
    private string _WriteTranscript(string toolUseId, string content)
    {
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        Directory.CreateDirectory(projectDir);
        var path = Path.Combine(projectDir, $"{SessionId}.jsonl");

        var line = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new
            {
                content = new[]
                {
                    new { type = "tool_result", tool_use_id = toolUseId, content },
                },
            },
        });

        File.WriteAllLines(path, ["""{"type":"assistant","message":{"content":[{"type":"text","text":"working"}]}}""", line]);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_configDir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
