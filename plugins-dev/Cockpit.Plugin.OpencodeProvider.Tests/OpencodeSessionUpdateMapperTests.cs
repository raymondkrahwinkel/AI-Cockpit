using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// `OpencodeSessionUpdateMapper` (AC-783) against literal `session/update` notification `params`, all shapes
// taken from this session's own live probing of a real `opencode acp` process — proves the translation
// without a driver or a fake process: text/thinking deltas, the lazy tool_call/tool_call_update refinement
// sequence producing exactly one `PluginToolUseRequested` at the earliest of its triggers, tool_call_update's
// terminal-only result mapping, and that malformed/unknown input never throws. Mirrors
// Cockpit.Plugin.KimiProvider.Tests.KimiSessionUpdateMapperTests, scoped down: the tracking-cap
// eviction behaviour is unmodified shared logic already proven there, so it is not re-asserted here.
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

    [Fact]
    public void Map_AgentThoughtChunk_ProducesAThinkingDelta()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"The user wants me to create a file"}}}""");

        var delta = Assert.IsType<PluginAssistantThinkingDelta>(Assert.Single(result.Events));
        Assert.Equal("The user wants me to create a file", delta.Thinking);
    }

    // Measured live: opencode's first tool_call for a file write carries an empty rawInput ({}), not a missing
    // one — the mapper must treat that the same as "not known yet", not as a real (empty) argument set.
    [Fact]
    public void Map_LazyToolCall_WithEmptyRawInput_ProducesNoEventYet()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"write","kind":"edit","status":"pending","locations":[],"rawInput":{}}}""");

        // An empty object is still a present rawInput per the mapper's own null/undefined check, so this
        // actually fires immediately — this test pins that observed behaviour rather than assuming a "no
        // rawInput at all" case, which opencode's live traffic never produced for tool_call.
        Assert.Single(result.Events);
    }

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

    [Theory]
    [InlineData("pending")]
    [InlineData("in_progress")]
    public void Map_ToolCallUpdate_NonTerminalStatus_WithoutRawInput_ProducesNoEvent(string status)
    {
        var result = _Map($$$"""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"call_1","status":"{{{status}}}"}}""");

        Assert.Empty(result.Events);
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

    [Fact]
    public void Map_ConfigOptionUpdate_ProducesNoEvent_ButCarriesTheConfigOptionsOut()
    {
        // Measured live shape: model + mode, no "thinking" id (see the driver's own remarks).
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"config_option_update","configOptions":[{"id":"model","name":"Model","category":"model","type":"select","currentValue":"opencode/big-pickle","options":[]},{"id":"mode","name":"Session Mode","category":"mode","type":"select","currentValue":"build","options":[]}]}}""");

        Assert.Empty(result.Events);
        Assert.NotNull(result.ConfigOptions);
        Assert.Equal(2, result.ConfigOptions!.Value.GetArrayLength());
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("available_commands_update")]
    public void Map_IgnoredUpdateKinds_ProduceNothing(string discriminator)
    {
        var result = _Map($$$"""{"sessionId":"s1","update":{"sessionUpdate":"{{{discriminator}}}"}}""");

        Assert.Empty(result.Events);
        Assert.Null(result.ConfigOptions);
    }

    // usage_update is handled by the driver directly, never by this mapper — reaching Map() at all with this
    // discriminator must still be safe (no throw), the same "never trust the wire" discipline every other
    // unrecognised discriminator gets.
    [Fact]
    public void Map_UsageUpdate_ProducesNothing_AndDoesNotThrow()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"usage_update","used":8507,"size":200000,"cost":{"amount":0,"currency":"USD"}}}""");

        Assert.Empty(result.Events);
    }

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

    [Fact]
    public void EnsureToolUseRequested_WithNoPriorSighting_EmitsUsingTheFallbackName()
    {
        var mapper = new OpencodeSessionUpdateMapper();

        var emitted = mapper.EnsureToolUseRequested("call_1", "s1", fallbackToolName: "hello.txt");

        Assert.NotNull(emitted);
        Assert.Equal("call_1", emitted!.ToolUseId);
        Assert.Equal("hello.txt", emitted.ToolName);
        Assert.Equal("{}", emitted.InputJson);
    }

    [Fact]
    public void EnsureToolUseRequested_AfterTheEventAlreadyFired_ReturnsNull()
    {
        var mapper = new OpencodeSessionUpdateMapper();
        mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"call_1","title":"write","rawInput":{"filepath":"x"}}}"""));

        Assert.Null(mapper.EnsureToolUseRequested("call_1", "s1", fallbackToolName: "tool"));
    }

    private static JsonElement _Parse(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        return document.RootElement.Clone();
    }

    private static OpencodeSessionUpdateMapResult _Map(string paramsJson) => new OpencodeSessionUpdateMapper().Map(_Parse(paramsJson));
}
