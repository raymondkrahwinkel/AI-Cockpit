using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>
/// <see cref="KimiSessionUpdateMapper"/> (AC-270 sub [c], P1-3) against literal <c>session/update</c>
/// notification <c>params</c> — proves the translation without a driver or a fake process: text/thinking
/// deltas, the lazy tool_call/tool_call_update refinement sequence producing exactly one
/// <see cref="PluginToolUseRequested"/> at the earliest of its three triggers, tool_call_update's terminal-only
/// result mapping, and that malformed/unknown input never throws. Each test gets its own mapper instance — the
/// class is stateful per toolCallId across the whole session's notification stream, so tests exercising a
/// specific arrival order call <see cref="KimiSessionUpdateMapper.Map"/> more than once on the same instance.
/// </summary>
public class KimiSessionUpdateMapperTests
{
    [Fact]
    public void Map_AgentMessageChunk_ProducesATextDelta()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hello"}}}""");

        var delta = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginAssistantTextDelta>().Subject;
        delta.SessionId.Should().Be("s1");
        delta.Text.Should().Be("Hello");
    }

    [Fact]
    public void Map_AgentThoughtChunk_ProducesAThinkingDelta()
    {
        // Reasoning must land as thinking, not as ordinary assistant text.
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"Let me consider"}}}""");

        var delta = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginAssistantThinkingDelta>().Subject;
        delta.Thinking.Should().Be("Let me consider");
    }

    // D4/P1-3, trigger (a): a tool_call with no rawInput yet is remembered but produces nothing on its own.
    [Fact]
    public void Map_LazyToolCall_WithoutRawInput_ProducesNoEventYet()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"pending"}}""");

        result.Events.Should().BeEmpty();
    }

    // Trigger (a): a tool_call that already carries rawInput fires the one PluginToolUseRequested immediately.
    [Fact]
    public void Map_ToolCallWithRawInput_ProducesToolUseRequested_CarryingItAsInputJson()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"in_progress","rawInput":{"path":"/x"}}}""");

        var toolUse = result.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolUseRequested>().Subject;
        toolUse.ToolUseId.Should().Be("turn-1:tool-1");
        toolUse.ToolName.Should().Be("Read");
        toolUse.InputJson.Should().Be("""{"path":"/x"}""");
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

        lazy.Events.Should().BeEmpty();
        var toolUse = refined.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolUseRequested>().Subject;
        toolUse.ToolName.Should().Be("Read file.txt");
        toolUse.InputJson.Should().Be("""{"path":"file.txt"}""");

        var terminal = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"done"}}]}}"""));
        terminal.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolResult>();
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("in_progress")]
    public void Map_ToolCallUpdate_NonTerminalStatus_WithoutRawInput_ProducesNoEvent(string status)
    {
        var result = _Map($$$"""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"{{{status}}}"}}""");

        result.Events.Should().BeEmpty();
    }

    // P1-3, trigger (c): a terminal update for an id that never got a prior tool_call (or refining update) must
    // still produce a PluginToolUseRequested first — otherwise the host has nothing to attach the tool result
    // to — followed by the PluginToolResult. This replaces the previous version of this test, which expected a
    // lone PluginToolResult: that was the wrong wire form once tool_call/tool_call_update ordering is uncertain.
    [Fact]
    public void Map_ToolCallUpdate_Completed_WithNoPriorToolCall_ProducesToolUseRequested_ThenToolResult_WithoutError()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"done"}}]}}""");

        result.Events.Should().HaveCount(2);
        result.Events[0].Should().BeOfType<PluginToolUseRequested>().Which.ToolUseId.Should().Be("turn-1:tool-1");
        var toolResult = result.Events[1].Should().BeOfType<PluginToolResult>().Subject;
        toolResult.ToolUseId.Should().Be("turn-1:tool-1");
        toolResult.Content.Should().Be("done");
        toolResult.IsError.Should().BeFalse();
    }

    [Fact]
    public void Map_ToolCallUpdate_Failed_WithNoPriorToolCall_ProducesToolUseRequested_ThenToolResult_WithError()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"failed","rawOutput":{"message":"boom"}}}""");

        result.Events.Should().HaveCount(2);
        result.Events[0].Should().BeOfType<PluginToolUseRequested>();
        var toolResult = result.Events[1].Should().BeOfType<PluginToolResult>().Subject;
        toolResult.IsError.Should().BeTrue();
        toolResult.Content.Should().Be("""{"message":"boom"}""");
    }

    // D5: content is REPLACE not APPEND — proving the mapper reads only the current update's content, never a
    // previous one it never saw.
    [Fact]
    public void Map_ToolCallUpdate_Completed_WithMultipleTextBlocks_ConcatenatesThem()
    {
        var mapper = new KimiSessionUpdateMapper();
        mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","rawInput":{}}}"""));
        var result = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"a"}},{"type":"content","content":{"type":"text","text":"b"}}]}}"""));

        result.Events.Single().Should().BeOfType<PluginToolResult>().Which.Content.Should().Be("ab");
    }

    // P1-3: once the one PluginToolUseRequested has fired, a further refining update for the same id must not
    // produce a second one — the plugin contract has no "update an already-requested tool call" event.
    [Fact]
    public void ToolCallWithRawInput_ThenAnotherToolCallUpdateWithRawInput_ProducesOnlyOneToolUseRequested()
    {
        var mapper = new KimiSessionUpdateMapper();
        var first = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","rawInput":{"path":"a"}}}"""));
        var second = mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call_update","toolCallId":"turn-1:tool-1","status":"in_progress","title":"Read again","rawInput":{"path":"b"}}}"""));

        first.Events.Should().ContainSingle().Which.Should().BeOfType<PluginToolUseRequested>();
        second.Events.Should().BeEmpty();
    }

    [Fact]
    public void Map_ConfigOptionUpdate_ProducesNoEvent_ButCarriesTheConfigOptionsOut()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"config_option_update","configOptions":[{"type":"select","id":"model","name":"Model","currentValue":"kimi-k2","options":[]}]}}""");

        result.Events.Should().BeEmpty();
        result.ConfigOptions.Should().NotBeNull();
        result.ConfigOptions!.Value.GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("available_commands_update")]
    public void Map_IgnoredUpdateKinds_ProduceNothing(string discriminator)
    {
        var result = _Map($$$"""{"sessionId":"s1","update":{"sessionUpdate":"{{{discriminator}}}"}}""");

        result.Events.Should().BeEmpty();
        result.ConfigOptions.Should().BeNull();
    }

    [Fact]
    public void Map_UnknownDiscriminator_ProducesNothing_AndDoesNotThrow()
    {
        var mapper = new KimiSessionUpdateMapper();
        var map = () => mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"something_kimi_added_later"}}"""));

        map.Should().NotThrow();
        map().Events.Should().BeEmpty();
    }

    [Fact]
    public void Map_NotificationWithoutParams_ProducesNothing_AndDoesNotThrow()
    {
        // The undefault(JsonElement) shape a param-less notification reaches the mapper as.
        var mapper = new KimiSessionUpdateMapper();
        var map = () => mapper.Map(default);

        map.Should().NotThrow();
        map().Events.Should().BeEmpty();
    }

    [Fact]
    public void Map_MalformedUpdate_MissingSessionUpdateField_ProducesNothing()
    {
        var result = _Map("""{"sessionId":"s1","update":{"someOtherField":true}}""");

        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void Map_ToolCall_WithoutToolCallId_ProducesNothing()
    {
        var result = _Map("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","title":"Read"}}""");

        result.Events.Should().BeEmpty();
    }

    // --- EnsureToolUseRequested (P1-3, trigger (c) for a permission request) ---------------------------------

    [Fact]
    public void EnsureToolUseRequested_WithNoPriorSighting_EmitsUsingTheFallbackName()
    {
        var mapper = new KimiSessionUpdateMapper();

        var emitted = mapper.EnsureToolUseRequested("turn-1:tool-1", "s1", fallbackToolName: "shell");

        emitted.Should().NotBeNull();
        emitted!.ToolUseId.Should().Be("turn-1:tool-1");
        emitted.ToolName.Should().Be("shell");
        emitted.InputJson.Should().Be("{}");
    }

    [Fact]
    public void EnsureToolUseRequested_AfterALazyToolCall_EmitsUsingWhatIsAlreadyKnown()
    {
        var mapper = new KimiSessionUpdateMapper();
        mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","status":"pending"}}"""));

        var emitted = mapper.EnsureToolUseRequested("turn-1:tool-1", "s1", fallbackToolName: "tool");

        emitted.Should().NotBeNull();
        emitted!.ToolName.Should().Be("Read");
    }

    [Fact]
    public void EnsureToolUseRequested_AfterTheEventAlreadyFired_ReturnsNull()
    {
        var mapper = new KimiSessionUpdateMapper();
        mapper.Map(_Parse("""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"turn-1:tool-1","title":"Read","rawInput":{}}}"""));

        mapper.EnsureToolUseRequested("turn-1:tool-1", "s1", fallbackToolName: "tool").Should().BeNull();
    }

    // Both maps are keyed on toolCallIds the child process invents and neither empties on its own, so a child
    // that keeps inventing them must not be able to grow the host's memory without a ceiling. Past the cap the
    // oldest id is forgotten instead.
    [Fact]
    public void ManyLazyToolCalls_PastTheTrackingCap_ForgetTheOldestInsteadOfGrowing()
    {
        var mapper = new KimiSessionUpdateMapper();
        var overflow = KimiSessionUpdateMapper.MaxTrackedToolCalls + 500;

        for (var index = 0; index < overflow; index++)
        {
            mapper.Map(_Parse($$$"""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"tool-{{{index}}}","title":"Read"}}"""));
        }

        mapper.TrackedToolCallCountForTests.Should().BeLessThanOrEqualTo(KimiSessionUpdateMapper.MaxTrackedToolCalls);

        // The most recent id is the one that still matters: its permission request must still find what the
        // mapper knows about it rather than falling back to a bare name.
        var latest = mapper.EnsureToolUseRequested($"tool-{overflow - 1}", "s1", fallbackToolName: "tool");
        latest!.ToolName.Should().Be("Read");
    }

    [Fact]
    public void ManyEmittedToolCalls_PastTheTrackingCap_ForgetTheOldestInsteadOfGrowing()
    {
        var mapper = new KimiSessionUpdateMapper();
        var overflow = KimiSessionUpdateMapper.MaxTrackedToolCalls + 500;

        for (var index = 0; index < overflow; index++)
        {
            mapper.Map(_Parse($$$$"""{"sessionId":"s1","update":{"sessionUpdate":"tool_call","toolCallId":"tool-{{{{index}}}}","title":"Read","rawInput":{}}}"""));
        }

        mapper.EmittedToolCallCountForTests.Should().BeLessThanOrEqualTo(KimiSessionUpdateMapper.MaxTrackedToolCalls);

        // Recent ids keep their "already emitted" answer — only ids thousands of calls old are forgotten.
        mapper.EnsureToolUseRequested($"tool-{overflow - 1}", "s1", fallbackToolName: "tool").Should().BeNull();
    }

    private static JsonElement _Parse(string paramsJson)
    {
        using var document = JsonDocument.Parse(paramsJson);
        return document.RootElement.Clone();
    }

    private static KimiSessionUpdateMapResult _Map(string paramsJson) => new KimiSessionUpdateMapper().Map(_Parse(paramsJson));
}
