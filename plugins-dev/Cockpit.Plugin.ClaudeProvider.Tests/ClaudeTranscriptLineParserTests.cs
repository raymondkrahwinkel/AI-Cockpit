using FluentAssertions;

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

        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text).Should().BeTrue();
        text.Should().Be("Hello world.");
    }

    [Fact]
    public void SkipsToolUseAndThinkingBlocks()
    {
        const string line = """
        {"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"},{"type":"tool_use","name":"Bash"}]}}
        """;

        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text).Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("""{"type":"user","message":{"content":[{"type":"text","text":"hi"}]}}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    public void ReturnsFalse_ForBlankNonAssistantOrUnparseableLines(string line)
    {
        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text).Should().BeFalse();
        text.Should().BeEmpty();
    }
}
