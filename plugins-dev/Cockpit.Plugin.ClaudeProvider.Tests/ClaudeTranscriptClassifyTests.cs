using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// The plugin's own transcript-line classification (ported from the host's former <c>TtyTranscriptStatus</c>,
/// weg A): a user message or tool-result means the model owes a response (Busy); an assistant message is Busy
/// while it streams or loops into a tool call and TurnComplete on a terminal stop_reason; metadata and
/// unparseable lines carry no signal.
/// </summary>
public class ClaudeTranscriptClassifyTests
{
    [Fact]
    public void UserLine_IsBusy_TheModelOwesAResponse() =>
        ClaudeTranscriptReader.ClassifyLine("""{"type":"user","message":{"role":"user"}}""")
            .Should().Be(PluginSessionActivity.Busy);

    [Fact]
    public void AssistantToolUse_IsBusy_ItWillLoop() =>
        ClaudeTranscriptReader.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}""")
            .Should().Be(PluginSessionActivity.Busy);

    [Fact]
    public void AssistantStillStreaming_IsBusy() =>
        ClaudeTranscriptReader.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":null}}""")
            .Should().Be(PluginSessionActivity.Busy);

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    [InlineData("max_tokens")]
    public void AssistantTerminalStop_IsTurnComplete(string stopReason) =>
        ClaudeTranscriptReader.ClassifyLine(
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"" + stopReason + "\"}}")
            .Should().Be(PluginSessionActivity.TurnComplete);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"summary"}""")]
    public void MetadataOrUnparseable_IsNone(string line) =>
        ClaudeTranscriptReader.ClassifyLine(line).Should().Be(PluginSessionActivity.None);
}
