using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// AC-783: `OpencodeSessionUpdateMapper` against literal `session/update` params, shapes taken from this
// session's own live probing. Mirrors KimiSessionUpdateMapperTests, minus the tracking-cap eviction tests —
// unmodified shared logic already proven there.
public class OpencodeSessionUpdateMapperTests
{
    [Fact]
    public void Map_AgentMessageChunk_ProducesATextDelta()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}""");

        var delta = Assert.IsType<PluginAssistantTextDelta>(Assert.Single(result.Events));
        Assert.Equal("s1", delta.SessionId);
        Assert.Equal("Hello", delta.Text);
    }

    // Measured live: opencode's first tool_call for a file write carries an empty rawInput ({}), not a missing
    // one — the mapper must treat that the same as "not known yet", not as a real (empty) argument set.

    [Fact]
    public void Map_ToolCallWithRawInput_ProducesToolUseRequested_CarryingItAsInputJson()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"write","status":"in_progress","rawInput":{"filepath":"hello.txt"}}}""");

        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(result.Events));
        Assert.Equal("call_1", toolUse.ToolUseId);
        Assert.Equal("write", toolUse.ToolName);
        Assert.Equal("""{"filepath":"hello.txt"}""", toolUse.InputJson);
    }

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

    [Fact]
    public void Map_ToolCallUpdate_Failed_WithNoPriorToolCall_ProducesToolUseRequested_ThenToolResult_WithError()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"failed","rawOutput":{"message":"boom"}}}""");

        Assert.Equal(2, result.Events.Count);
        Assert.IsType<PluginToolUseRequested>(result.Events[0]);
        var toolResult = Assert.IsType<PluginToolResult>(result.Events[1]);
        Assert.True(toolResult.IsError);
        Assert.Equal("""{"message":"boom"}""", toolResult.Content);
    }

    // usage_update is handled by the driver directly, never by this mapper — reaching Map() at all with this
    // discriminator must still be safe (no throw), the same "never trust the wire" discipline every other
    // unrecognised discriminator gets.

    [Fact]
    public void Map_UnknownDiscriminator_ProducesNothing_AndDoesNotThrow()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"something_opencode_added_later"}}""");

        Assert.Empty(result.Events);
    }

    [Fact]
    public void Map_MalformedUpdate_MissingSessionUpdateField_ProducesNothing()
    {
        var result = _Map("""{"sessionId":"s1","update":{"someOtherField":true}}""");

        Assert.Empty(result.Events);
    }

    // --- EnsureToolUseRequested (trigger for a permission request outside the session/update stream) --------

    private static JsonElement _Parse(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        return document.RootElement.Clone();
    }

    private static OpencodeSessionUpdateMapResult _Map(string paramsJson) => new OpencodeSessionUpdateMapper().Map(_Parse(paramsJson));
}
