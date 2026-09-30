
namespace Cockpit.Plugin.TranscriptSearch.Tests;

// Pulling searchable prose out of a transcript JSONL line (#9): a plain-string user prompt, the text blocks
// of an assistant message (skipping thinking/tool-use), the role resolution, and the null cases (tool-result,
// non-message, malformed).
public class TranscriptTextExtractorTests
{
    [Fact]
    public void Extract_UserStringContent_ReturnsThePrompt()
    {
        var line = """{"type":"user","message":{"role":"user","content":"fix the login bug"}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        Assert.NotNull(entry);
        Assert.Equal("user", entry!.Role);
        Assert.Equal("fix the login bug", entry.Text);
    }

    [Fact]
    public void Extract_AssistantTextBlocks_ConcatenatesOnlyTextBlocks()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Here is the fix"},{"type":"tool_use","name":"Edit","input":{}}]}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        Assert.NotNull(entry);
        Assert.Equal("assistant", entry!.Role);
        Assert.Equal("Here is the fix", entry.Text);
    }

    [Fact]
    public void Extract_ToolResultUserRecord_ReturnsNull()
    {
        var line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}""";

        Assert.Null(TranscriptTextExtractor.Extract(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Extract_BlankOrMalformed_ReturnsNull(string line)
        => Assert.Null(TranscriptTextExtractor.Extract(line));
}
