using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

// The plugin's own transcript-line classification (ported from the host's former `TtyTranscriptStatus`,
// weg A): a user message or tool-result means the model owes a response (Busy); an assistant message is Busy
// while it streams or loops into a tool call and TurnComplete on a terminal stop_reason; metadata and
// unparseable lines carry no signal.
public class ClaudeTranscriptClassifyTests
{
    [Fact]
    public void UserLine_IsBusy_TheModelOwesAResponse() =>
        Assert.Equal(PluginSessionActivity.Busy, ClaudeTranscriptReader.ClassifyLine("""{"type":"user","message":{"role":"user"}}"""));

    [Fact]
    public void AssistantToolUse_IsBusy_ItWillLoop() =>
        Assert.Equal(PluginSessionActivity.Busy, ClaudeTranscriptReader.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}"""));

    [Fact]
    public void AssistantStillStreaming_IsBusy() =>
        Assert.Equal(PluginSessionActivity.Busy, ClaudeTranscriptReader.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":null}}"""));

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    [InlineData("max_tokens")]
    public void AssistantTerminalStop_IsTurnComplete(string stopReason) =>
        Assert.Equal(PluginSessionActivity.TurnComplete, ClaudeTranscriptReader.ClassifyLine(
                "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"" + stopReason + "\"}}"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not valid json")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    [InlineData("""{"type":"summary"}""")]
    public void MetadataOrUnparseable_IsNone(string line) =>
        Assert.Equal(PluginSessionActivity.None, ClaudeTranscriptReader.ClassifyLine(line));
}
