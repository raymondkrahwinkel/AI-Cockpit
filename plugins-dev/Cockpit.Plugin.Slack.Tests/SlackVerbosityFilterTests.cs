using Cockpit.Plugins.Abstractions.Channels;

namespace Cockpit.Plugin.Slack.Tests;

public class SlackVerbosityFilterTests
{
    [Theory]
    [InlineData(AssistantChannelRowKind.AssistantText, true)]
    [InlineData(AssistantChannelRowKind.TurnCompleted, true)]
    [InlineData(AssistantChannelRowKind.Error, true)]
    [InlineData(AssistantChannelRowKind.ToolUse, false)]
    [InlineData(AssistantChannelRowKind.ToolResult, false)]
    [InlineData(AssistantChannelRowKind.Thinking, false)]
    [InlineData(AssistantChannelRowKind.UserText, false)]
    [InlineData(AssistantChannelRowKind.Question, false)]
    [InlineData(AssistantChannelRowKind.Divider, false)]
    public void FinalAnswerOnly_RelaysOnlyTheAnswerAndErrors(AssistantChannelRowKind kind, bool expected) =>
        Assert.Equal(expected, SlackVerbosityFilter.ShouldRelay(kind, AssistantChannelVerbosity.FinalAnswerOnly));

    [Theory]
    [InlineData(AssistantChannelRowKind.AssistantText, true)]
    [InlineData(AssistantChannelRowKind.ToolUse, true)]
    [InlineData(AssistantChannelRowKind.ToolResult, true)]
    [InlineData(AssistantChannelRowKind.Thinking, true)]
    [InlineData(AssistantChannelRowKind.UserText, true)]
    [InlineData(AssistantChannelRowKind.Divider, false)]
    public void Everything_RelaysEverythingButTheDivider(AssistantChannelRowKind kind, bool expected) =>
        Assert.Equal(expected, SlackVerbosityFilter.ShouldRelay(kind, AssistantChannelVerbosity.Everything));

    [Theory]
    [InlineData(AssistantChannelRowKind.ToolUse, true)]
    [InlineData(AssistantChannelRowKind.Divider, false)]
    public void StatusLines_RelaysTheSameSetAsEverything(AssistantChannelRowKind kind, bool expected) =>
        Assert.Equal(expected, SlackVerbosityFilter.ShouldRelay(kind, AssistantChannelVerbosity.StatusLines));

    [Fact]
    public void Everything_RendersRowTextVerbatim()
    {
        var row = _Row(AssistantChannelRowKind.ToolUse, "the full tool call text", toolName: "git status");

        Assert.Equal("the full tool call text", SlackVerbosityFilter.Render(row, AssistantChannelVerbosity.Everything));
    }

    [Fact]
    public void StatusLines_CollapsesToolUseIntoAShortLine()
    {
        var row = _Row(AssistantChannelRowKind.ToolUse, "the full tool call text", toolName: "git status");

        var rendered = SlackVerbosityFilter.Render(row, AssistantChannelVerbosity.StatusLines);

        Assert.Contains("git status", rendered);
        Assert.DoesNotContain("the full tool call text", rendered);
    }

    [Fact]
    public void StatusLines_LeavesAssistantTextFullLength()
    {
        var row = _Row(AssistantChannelRowKind.AssistantText, "the assistant's actual answer");

        Assert.Equal("the assistant's actual answer", SlackVerbosityFilter.Render(row, AssistantChannelVerbosity.StatusLines));
    }

    private static AssistantChannelRow _Row(AssistantChannelRowKind kind, string text, string? toolName = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Text = text,
        Timestamp = DateTimeOffset.UtcNow,
        ToolName = toolName,
    };
}
