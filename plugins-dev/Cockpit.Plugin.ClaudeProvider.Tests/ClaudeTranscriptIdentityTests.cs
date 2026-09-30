using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// AC-609: which file is *this* session's transcript, and reading it back.
//
// The bug these guard is not a wrong reading, it is no reading at all. The reader used to identify a session's
// transcript as the newest `.jsonl` under the config dir that was not there at launch — a race against every
// other invocation of the CLI on the machine, several of which write one short file and exit. A launch that
// latched onto one of those tailed a file that never grew again: no activity ever reached the host, so its
// tracker reported the "before any signal" status — Idle — for the whole life of a session working normally, and
// nothing timed out because nothing had started. On the reported case the foreign file was created 18 seconds
// before the session's own.
public class ClaudeTranscriptIdentityTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-transcript-identity-tests-").FullName;

    private static readonly IReadOnlySet<string> NoBaseline = new HashSet<string>();

    [Fact]
    public void ReadEntries_ReadsBackWhatTheSessionWrote_PairingEachToolResultWithItsCall()
    {
        var transcript = _CreateTranscript("read-back");
        File.WriteAllLines(
            transcript,
            [
                _UserLine("find the bug"),
                """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"where would it be"},{"type":"text","text":"Looking now."},{"type":"tool_use","id":"toolu_1","name":"Grep","input":{"pattern":"latch"}}]}}""",
                """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":[{"type":"text","text":"three hits"}]}]}}""",
                // Bookkeeping the CLI writes between messages — not a row anybody asked about.
                """{"type":"ai-title","title":"Finding the bug"}""",
            ]);

        var slice = new ClaudeTranscriptReader().ReadEntries(_WriteStatusFile(transcript), count: 30);

        Assert.Equal(4, slice.TotalEntries);
        Assert.Equal(
            [
                PluginTranscriptEntryKind.UserText,
                PluginTranscriptEntryKind.Thinking,
                PluginTranscriptEntryKind.AssistantText,
                PluginTranscriptEntryKind.ToolUse,
            ],
            slice.Entries.Select(entry => entry.Kind));
        Assert.Equal("find the bug", slice.Entries[0].Text);
        Assert.Contains("Grep", slice.Entries[3].Text);
        Assert.Equal("three hits", slice.Entries[3].ToolResult);
    }

    [Fact]
    public void ReadEntries_KeepsOnlyTheLastRowsAsked_AndStillReportsTheTotal()
    {
        // A tail has to be reportable as a tail: a caller told it has thirty rows out of thirty reads them as the
        // whole conversation and says so out loud.
        var transcript = _CreateTranscript("long");
        File.WriteAllLines(transcript, Enumerable.Range(0, 10).Select(index => _UserLine($"line {index}")));

        var slice = new ClaudeTranscriptReader().ReadEntries(_WriteStatusFile(transcript), count: 3);

        Assert.Equal(10, slice.TotalEntries);
        Assert.Equal(["line 7", "line 8", "line 9"], slice.Entries.Select(entry => entry.Text));
    }

    [Fact]
    public void ReadEntries_WithoutAStatusFile_ReportsNothing()
    {
        // There is no honest guess to make here. An empty answer is a session that has written nothing this reader
        // can name; a guess is somebody else's conversation handed over as this session's.
        Assert.Empty(new ClaudeTranscriptReader().ReadEntries(statusFile: null, count: 30).Entries);
    }

    private string _CreateTranscript(string name)
    {
        var projectDir = Path.Combine(_configDir, "projects", "some-cwd-hash");
        Directory.CreateDirectory(projectDir);
        var path = Path.Combine(projectDir, $"{name}.jsonl");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    // <summary>The statusline snapshot the CLI writes for a session, of which only `transcript_path` matters here.</summary>
    private string _WriteStatusFile(string transcriptPath, string? at = null)
    {
        var path = at ?? Path.Combine(_configDir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { transcript_path = transcriptPath }));
        return path;
    }

    private static string _UserLine(string text) =>
        JsonSerializer.Serialize(new { type = "user", message = new { content = text } });

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
