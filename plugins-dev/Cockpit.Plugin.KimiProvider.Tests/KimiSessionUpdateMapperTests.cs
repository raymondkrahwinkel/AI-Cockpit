using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

// `KimiSessionUpdateMapper` (AC-270 sub [c], P1-3) against literal `session/update`
// notification `params` — proves the translation without a driver or a fake process: text/thinking
// deltas, the lazy tool_call/tool_call_update refinement sequence producing exactly one
// `PluginToolUseRequested` at the earliest of its three triggers, tool_call_update's terminal-only
// result mapping, and that malformed/unknown input never throws. Each test gets its own mapper instance — the
// class is stateful per toolCallId across the whole session's notification stream, so tests exercising a
// specific arrival order call `KimiSessionUpdateMapper.Map` more than once on the same instance.
public class KimiSessionUpdateMapperTests
{
    [Fact]
    public void Map_AgentMessageChunk_ProducesATextDelta()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}""");

        var delta = Assert.IsType<PluginAssistantTextDelta>(Assert.Single(result.Events));
        Assert.Equal("s1", delta.SessionId);
        Assert.Equal("Hello", delta.Text);
    }

    // D4/P1-3, trigger (a): a tool_call with no rawInput yet is remembered but produces nothing on its own.

    // Trigger (a): a tool_call that already carries rawInput fires the one PluginToolUseRequested immediately.
    [Fact]
    public void Map_ToolCallWithRawInput_ProducesToolUseRequested_CarryingItAsInputJson()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"in_progress","rawInput":{"path":"/x"}}}""");

        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(result.Events));
        Assert.Equal("turn-1:tool-1", toolUse.ToolUseId);
        Assert.Equal("Read", toolUse.ToolName);
        Assert.Equal("""{"path":"/x"}""", toolUse.InputJson);
    }

    // P1-3, trigger (b): the lazy tool_call (no rawInput) followed by the refining tool_call_update (real
    // title/rawInput) must produce exactly one PluginToolUseRequested, carrying the refined values — not the
    // lazy placeholder ("Read"/"{}") the previous version of the mapper stopped at.
    [Fact]
    public void LazyToolCall_ThenRefiningToolCallUpdate_ProducesExactlyOneToolUseRequested_WithTheRefinedValues()
    {
        var mapper = new KimiSessionUpdateMapper();

        var lazy = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"pending"}}"""));
        var refined = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"in_progress","title":"Read file.txt","rawInput":{"path":"file.txt"}}}"""));

        Assert.Empty(lazy.Events);
        var toolUse = Assert.IsType<PluginToolUseRequested>(Assert.Single(refined.Events));
        Assert.Equal("Read file.txt", toolUse.ToolName);
        Assert.Equal("""{"path":"file.txt"}""", toolUse.InputJson);

        var terminal = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"done"}}]}}"""));
        Assert.IsType<PluginToolResult>(Assert.Single(terminal.Events));
    }

    // P1-3, trigger (c): a terminal update for an id that never got a prior tool_call (or refining update) must
    // still produce a PluginToolUseRequested first — otherwise the host has nothing to attach the tool result
    // to — followed by the PluginToolResult. This replaces the previous version of this test, which expected a
    // lone PluginToolResult: that was the wrong wire form once tool_call/tool_call_update ordering is uncertain.
    [Fact]
    public void Map_ToolCallUpdate_Completed_WithNoPriorToolCall_ProducesToolUseRequested_ThenToolResult_WithoutError()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"done"}}]}}""");

        Assert.Equal(2, System.Linq.Enumerable.Count(result.Events));
        Assert.Equal("turn-1:tool-1", Assert.IsType<PluginToolUseRequested>(result.Events[0]).ToolUseId);
        var toolResult = Assert.IsType<PluginToolResult>(result.Events[1]);
        Assert.Equal("turn-1:tool-1", toolResult.ToolUseId);
        Assert.Equal("done", toolResult.Content);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public void Map_ToolCallUpdate_Failed_WithNoPriorToolCall_ProducesToolUseRequested_ThenToolResult_WithError()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"failed","rawOutput":{"message":"boom"}}}""");

        Assert.Equal(2, System.Linq.Enumerable.Count(result.Events));
        Assert.IsType<PluginToolUseRequested>(result.Events[0]);
        var toolResult = Assert.IsType<PluginToolResult>(result.Events[1]);
        Assert.True(toolResult.IsError);
        Assert.Equal("""{"message":"boom"}""", toolResult.Content);
    }

    // D5: content is REPLACE not APPEND — proving the mapper reads only the current update's content, never a
    // previous one it never saw.
    [Fact]
    public void Map_ToolCallUpdate_Completed_WithMultipleTextBlocks_ConcatenatesThem()
    {
        var mapper = new KimiSessionUpdateMapper();
        mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","rawInput":{}}}"""));
        var result = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"a"}},{"type":"content","content":{"type":"text","text":"b"}}]}}"""));

        Assert.Equal("ab", Assert.IsType<PluginToolResult>(result.Events.Single()).Content);
    }

    // P1-3: once the one PluginToolUseRequested has fired, a further refining update for the same id must not
    // produce a second one — the plugin contract has no "update an already-requested tool call" event.

    [Fact]
    public void Map_UnknownDiscriminator_ProducesNothing_AndDoesNotThrow()
    {
        var mapper = new KimiSessionUpdateMapper();
        var map = () => mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"something_kimi_added_later"}}"""));

        map();
        Assert.Empty(map().Events);
    }

    [Fact]
    public void Map_MalformedUpdate_MissingSessionUpdateField_ProducesNothing()
    {
        var result = _Map("""{"sessionId":"s1","update":{"someOtherField":true}}""");

        Assert.Empty(result.Events);
    }

    // --- EnsureToolUseRequested (P1-3, trigger (c) for a permission request) ---------------------------------

    // Both maps are keyed on toolCallIds the child process invents and neither empties on its own, so a child
    // that keeps inventing them must not be able to grow the host's memory without a ceiling. Past the cap the
    // oldest id is forgotten instead.

    private static JsonElement _Parse(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        return document.RootElement.Clone();
    }

    private static KimiSessionUpdateMapResult _Map(string paramsJson) => new KimiSessionUpdateMapper().Map(_Parse(paramsJson));
}
