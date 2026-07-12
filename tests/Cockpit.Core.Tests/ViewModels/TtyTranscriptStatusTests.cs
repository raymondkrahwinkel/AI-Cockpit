using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>Classifying a Claude-CLI transcript JSONL line into a TTY turn-progress signal (busy / done / ignore) — the input to <see cref="TtyActivityStatusTracker"/>.</summary>
public class TtyTranscriptStatusTests
{
    [Fact]
    public void ClassifyLine_UserMessage_IsInProgress()
    {
        TtyTranscriptStatus.ClassifyLine("""{"type":"user","message":{"role":"user"}}""").Should().Be(true);
    }

    [Fact]
    public void ClassifyLine_AssistantLoopingIntoAToolCall_IsInProgress()
    {
        TtyTranscriptStatus.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":"tool_use"}}""").Should().Be(true);
    }

    [Fact]
    public void ClassifyLine_AssistantStillStreaming_IsInProgress()
    {
        TtyTranscriptStatus.ClassifyLine("""{"type":"assistant","message":{"role":"assistant","stop_reason":null}}""").Should().Be(true);
    }

    [Theory]
    [InlineData("end_turn")]
    [InlineData("stop_sequence")]
    [InlineData("max_tokens")]
    public void ClassifyLine_AssistantWithATerminalStopReason_IsCompleted(string stopReason)
    {
        TtyTranscriptStatus.ClassifyLine($$"""{"type":"assistant","message":{"role":"assistant","stop_reason":"{{stopReason}}"}}""").Should().Be(false);
    }

    [Theory]
    [InlineData("""{"type":"last-prompt"}""")]
    [InlineData("""{"type":"custom-title"}""")]
    [InlineData("""{"type":"summary"}""")]
    [InlineData("""{"type":"system","subtype":"init"}""")]
    public void ClassifyLine_MetadataOrSystem_IsIgnored(string line)
    {
        TtyTranscriptStatus.ClassifyLine(line).Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[1,2,3]")]
    public void ClassifyLine_BlankUnparseableOrTypeless_IsIgnored(string? line)
    {
        TtyTranscriptStatus.ClassifyLine(line).Should().BeNull();
    }
}
