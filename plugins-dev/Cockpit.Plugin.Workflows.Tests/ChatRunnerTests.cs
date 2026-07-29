using System.Text.Json;
using Cockpit.Plugin.Workflows.Engine;

namespace Cockpit.Plugin.Workflows.Tests;

/// <summary>
/// What Slack and Discord are actually sent (#69). They differ in one thing — Slack takes <c>text</c>, Discord takes
/// <c>content</c> — and in one limit, which is the one that bites: Discord refuses a message over two thousand
/// characters outright, and two thousand characters is exactly the size a command's output reaches on the day the flow
/// matters.
/// </summary>
public class ChatRunnerTests
{
    [Fact]
    public void Slack_TakesText() =>
        Assert.Equal("deployed", _Field(ChatRunner.Body("deployed", discord: false), "text"));

    [Fact]
    public void Discord_TakesContent() =>
        Assert.Equal("deployed", _Field(ChatRunner.Body("deployed", discord: true), "content"));

    [Fact]
    public void AMessageTooLongForDiscord_IsCutAndVisiblySo_RatherThanRefusedWhole()
    {
        var sent = _Field(ChatRunner.Body(new string('x', 3000), discord: true), "content");

        Assert.Equal(ChatRunner.DiscordLimit, sent.Length);
        Assert.EndsWith("…", sent);
    }

    [Fact]
    public void ALongMessageToSlack_IsLeftAlone_BecauseSlackTakesIt() =>
        Assert.Equal(3000, _Field(ChatRunner.Body(new string('x', 3000), discord: false), "text").Length);

    private static string _Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetString()!;
}
