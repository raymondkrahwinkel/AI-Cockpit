
namespace Cockpit.Plugin.TranscriptSearch.Tests;

// Pulling searchable prose out of a transcript JSONL line (#9): a plain-string user prompt, the text blocks
// of an assistant message (skipping thinking/tool-use), the role resolution, and the null cases (tool-result,
// non-message, malformed).
public class TranscriptTextExtractorTests
{

    [Fact]
    public void Extract_AssistantTextBlocks_ConcatenatesOnlyTextBlocks()
    {
        var line = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Here is the fix"},{"type":"tool_use","name":"Edit","input":{}}]}}""";

        var entry = TranscriptTextExtractor.Extract(line);

        Assert.NotNull(entry);
        Assert.Equal("assistant", entry!.Role);
        Assert.Equal("Here is the fix", entry.Text);

        // A tool-result user record is no prose at all.
        Assert.Null(TranscriptTextExtractor.Extract("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}"""));
    }
}
