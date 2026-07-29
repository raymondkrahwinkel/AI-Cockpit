using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// The plugin's own transcript-line parser (weg A / Fase 4) — extracts spoken-worthy assistant text from a
/// Claude JSONL transcript line for the host's read-aloud, and stays silent on everything else (tool-use,
/// thinking, non-assistant lines, and mid-write garbage a tail read can land on).
/// </summary>
public class ClaudeTranscriptLineParserTests
{
    [Fact]
    public void ExtractsConcatenatedTextBlocks_FromAnAssistantLine()
    {
        const string line = """
        {"type":"assistant","message":{"content":[{"type":"text","text":"Hello "},{"type":"text","text":"world."}]}}
        """;

        Assert.True(ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text));
        Assert.Equal("Hello world.", text);
    }

    [Fact]
    public void SkipsToolUseAndThinkingBlocks()
    {
        const string line = """
        {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"tool_use","name":"Bash"}]}}
        """;

        Assert.False(ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text));
        Assert.Empty(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("""{"type":"user","message":{"content":[{"type":"text","text":"hi"}]}}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    public void ReturnsFalse_ForBlankNonAssistantOrUnparseableLines(string line)
    {
        Assert.False(ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text));
        Assert.Empty(text);
    }

    // --- TryExtractUsage (AC-398) --------------------------------------------------------------------------

    [Fact]
    public void TryExtractUsage_ReadsEveryTokenBucket_FromAnAssistantLine()
    {
        const string line = """
        {"type":"assistant","message":{"id":"msg_01","usage":{"input_tokens":120,"output_tokens":340,"cache_read_input_tokens":50,"cache_creation_input_tokens":7}}}
        """;

        Assert.True(ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out var messageId));
        Assert.Equal(new PluginTokenUsage(120, 340, 50, 7), usage);
        Assert.Equal("msg_01", messageId);
    }

    [Fact]
    public void TryExtractUsage_MissingBucket_ReadsAsZero_RatherThanFailing()
    {
        // A real assistant line can omit cache buckets entirely on a turn that read/wrote no cache — the point
        // of a missing property is "none", not "unparseable".
        const string line = """{"type":"assistant","message":{"id":"msg_01","usage":{"input_tokens":10,"output_tokens":5}}}""";

        Assert.True(ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out _));
        Assert.Equal(new PluginTokenUsage(10, 5, 0, 0), usage);
    }

    [Fact]
    public void TryExtractUsage_NonIntegralOrOutOfRangeTokenCount_ReadsAsZero_RatherThanThrowing()
    {
        // A malformed/unexpected number in the usage object must not blow up the tail's async iterator.
        const string line = """
        {"type":"assistant","message":{"id":"msg_01","usage":{"input_tokens":10.5,"output_tokens":99999999999}}}
        """;

        Assert.True(ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out _));
        Assert.Equal(new PluginTokenUsage(0, 0, 0, 0), usage);
    }

    [Fact]
    public void TryExtractUsage_MissingMessageId_ReadsAsNull_RatherThanFailing()
    {
        const string line = """{"type":"assistant","message":{"usage":{"input_tokens":10,"output_tokens":5}}}""";

        Assert.True(ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out var messageId));
        Assert.Equal(new PluginTokenUsage(10, 5, 0, 0), usage);
        Assert.Null(messageId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"text","text":"no usage here"}]}}""")]
    [InlineData("""{"type":"user","message":{"usage":{"input_tokens":10,"output_tokens":5}}}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    public void TryExtractUsage_ReturnsFalse_ForBlankNonAssistantOrUsagelessLines(string line)
    {
        Assert.False(ClaudeTranscriptLineParser.TryExtractUsage(line, out var usage, out var messageId));
        Assert.Null(usage);
        Assert.Null(messageId);
    }
}
