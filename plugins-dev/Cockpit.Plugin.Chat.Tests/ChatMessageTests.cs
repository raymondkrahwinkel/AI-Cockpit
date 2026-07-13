using System.Text.Json;
using FluentAssertions;

namespace Cockpit.Plugin.Chat.Tests;

/// <summary>
/// What the two services are actually sent. Slack takes <c>text</c>, Discord takes <c>content</c>, and Discord throws
/// the whole message away above two thousand characters — which is exactly the size a command's output reaches on the
/// day the flow matters.
/// </summary>
public class ChatMessageTests
{
    [Fact]
    public void Slack_TakesText() =>
        _Field(ChatMessage.Body(ChatService.Slack, "deployed"), "text").Should().Be("deployed");

    [Fact]
    public void Discord_TakesContent() =>
        _Field(ChatMessage.Body(ChatService.Discord, "deployed"), "content").Should().Be("deployed");

    [Fact]
    public void AMessageTooLongForDiscord_IsCutAndSaysSo_RatherThanBeingRefusedWhole()
    {
        // Discord answers 400 for anything over 2000 characters. A step that failed because the command it was
        // reporting on had a lot to say is a step that fails at the worst possible moment.
        var sent = _Field(ChatMessage.Body(ChatService.Discord, new string('x', 3000)), "content");

        sent.Should().HaveLength(ChatMessage.DiscordLimit);
        sent.Should().EndWith("…");
    }

    [Fact]
    public void ALongMessageToSlack_IsLeftAlone_BecauseSlackTakesIt() =>
        _Field(ChatMessage.Body(ChatService.Slack, new string('x', 3000)), "text").Should().HaveLength(3000);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyMessage_IsRefusedHere_RatherThanAs400FromTheService(string message)
    {
        var body = () => ChatMessage.Body(ChatService.Slack, message);

        body.Should().Throw<InvalidOperationException>().WithMessage("*no message*");
    }

    private static string _Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetString()!;
}
