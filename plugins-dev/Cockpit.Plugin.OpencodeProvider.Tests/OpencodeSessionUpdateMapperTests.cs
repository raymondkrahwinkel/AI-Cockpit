using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// AC-783: `OpencodeSessionUpdateMapper` against literal `session/update` params, shapes taken from this
// session's own live probing. Mirrors KimiSessionUpdateMapperTests, minus the tracking-cap eviction tests —
// unmodified shared logic already proven there.
public class OpencodeSessionUpdateMapperTests
{

    // Measured live sequence for a file write: tool_call (title="write", no rawInput) -> tool_call_update
    // (in_progress, refined title/rawInput/diff) -> tool_call_update (completed, content="Wrote file successfully.").
    [Fact]
    public void LazyToolCall_ThenRefiningUpdate_ThenTerminalUpdate_ProducesExactlyOneToolUseRequested_ThenAResult()
    {
        var mapper = new OpencodeSessionUpdateMapper();

        var lazy = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"write","kind":"edit","status":"pending","locations":[]}}"""));
        Assert.Empty(lazy.Events);

        var refined = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"in_progress","kind":"edit","title":"write","rawInput":{"filepath":"hello.txt","content":"hello world"}}}"""));
        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(refined.Events));
        Assert.Equal("""{"filepath":"hello.txt","content":"hello world"}""", toolUse.InputJson);

        var terminal = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"completed","title":"hello.txt","content":[{"type":"content","content":{"type":"text","text":"Wrote file successfully."}}]}}"""));
        var result = Assert.IsType<PluginToolResult>(Assert.Single(terminal.Events));
        Assert.Equal("Wrote file successfully.", result.Content);
        Assert.False(result.IsError);
    }

    // --- EnsureToolUseRequested (trigger for a permission request outside the session/update stream) --------

    private static JsonElement _Parse(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        return document.RootElement.Clone();
    }
}
