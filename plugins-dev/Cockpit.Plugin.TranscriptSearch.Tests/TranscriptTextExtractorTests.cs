using FluentAssertions;

namespace Cockpit.Plugin.TranscriptSearch.Tests;

/// <summary>
/// Pulling searchable prose out of a transcript JSONL line (#9): a plain-string user prompt, the text blocks
/// of an assistant message (skipping thinking/tool-use), the role resolution, and the null cases (tool-result,
/// non-message, malformed).
/// </summary>
public class TranscriptTextExtractorTests
{
    [Fact]
    public void Extract_UserStringContent_ReturnsThePrompt()
    {
        var line = """{"type":"user","message":{"role":"user","content":"fix the login bug"}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        entry.Should().NotBeNull();
        entry!.Role.Should().Be("user");
        entry.Text.Should().Be("fix the login bug");
    }

    [Fact]
    public void Extract_AssistantTextBlocks_ConcatenatesOnlyTextBlocks()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Here is the fix"},{"type":"tool_use","name":"Edit","input":{}}]}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        entry.Should().NotBeNull();
        entry!.Role.Should().Be("assistant");
        entry.Text.Should().Be("Here is the fix");
    }

    [Fact]
    public void Extract_ToolResultUserRecord_ReturnsNull()
    {
        var line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";

        TranscriptTextExtractor.Extract(line).Should().BeNull();
    }

    [Fact]
    public void Extract_NonMessageType_ReturnsNull()
    {
        TranscriptTextExtractor.Extract("""{"type":"summary","summary":"…"}""").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Extract_BlankOrMalformed_ReturnsNull(string line)
        => TranscriptTextExtractor.Extract(line).Should().BeNull();

    [Fact]
    public void Extract_RoleFromMessageRoleWhenTopLevelTypeIsNotARole()
    {
        // Some records carry the role only on message.role, with a different top-level "type".
        var line = """{"type":"message","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        entry.Should().NotBeNull();
        entry!.Role.Should().Be("assistant");
        entry.Text.Should().Be("hi");
    }
}
