using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="ClaudeTranscriptLineParser"/> (#35b): pulls the spoken-worthy text out of one line of a
/// <c>claude</c> session's live JSONL transcript — only <c>assistant</c> lines' <c>text</c> content
/// blocks, never <c>tool_use</c>/<c>thinking</c> blocks or <c>user</c>/<c>system</c> lines.
/// </summary>
public class ClaudeTranscriptLineParserTests
{
    [Fact]
    public void TryExtractAssistantText_AssistantTextLine_ExtractsTheText()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Here is the answer."}]}}""";

        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        extracted.Should().BeTrue();
        text.Should().Be("Here is the answer.");
    }

    [Fact]
    public void TryExtractAssistantText_MultipleTextBlocks_ConcatenatesThemInOrder()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"First part."},{"type":"text","text":"Second part."}]}}""";

        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        text.Should().Be("First part.Second part.");
    }

    [Fact]
    public void TryExtractAssistantText_ToolUseOnlyTurn_ReturnsFalse()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Bash","input":{"command":"ls"}}]}}""";

        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        extracted.Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractAssistantText_TextAndToolUseMixed_ExtractsOnlyTheTextBlocks()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Running the command now."},{"type":"tool_use","name":"Bash","input":{}}]}}""";

        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        text.Should().Be("Running the command now.");
    }

    [Fact]
    public void TryExtractAssistantText_ThinkingBlock_IsSkipped()
    {
        var line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Let me consider..."},{"type":"text","text":"Final answer."}]}}""";

        ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        text.Should().Be("Final answer.");
    }

    [Fact]
    public void TryExtractAssistantText_UserLine_ReturnsFalse()
    {
        var line = """{"type":"user","message":{"content":[{"type":"text","text":"a user message"}]}}""";

        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        extracted.Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractAssistantText_SystemLine_ReturnsFalse()
    {
        var line = """{"type":"system","subtype":"init","cwd":"/repo"}""";

        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText(line, out var text);

        extracted.Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractAssistantText_BlankLine_ReturnsFalse()
    {
        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText("   ", out var text);

        extracted.Should().BeFalse();
        text.Should().BeEmpty();
    }

    [Fact]
    public void TryExtractAssistantText_MalformedJson_ReturnsFalseRatherThanThrowing()
    {
        var extracted = ClaudeTranscriptLineParser.TryExtractAssistantText("""{"type":"assistant","message":{"content":[{"type":"text","text":"cut off mid""", out var text);

        extracted.Should().BeFalse();
        text.Should().BeEmpty();
    }
}
