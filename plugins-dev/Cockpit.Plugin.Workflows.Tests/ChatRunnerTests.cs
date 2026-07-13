using System.Text.Json;
using Cockpit.Plugin.Workflows.Engine;
using FluentAssertions;

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
        _Field(ChatRunner.Body("deployed", discord: false), "text").Should().Be("deployed");

    [Fact]
    public void Discord_TakesContent() =>
        _Field(ChatRunner.Body("deployed", discord: true), "content").Should().Be("deployed");

    [Fact]
    public void AMessageTooLongForDiscord_IsCutAndVisiblySo_RatherThanRefusedWhole()
    {
        var sent = _Field(ChatRunner.Body(new string('x', 3000), discord: true), "content");

        sent.Should().HaveLength(ChatRunner.DiscordLimit);
        sent.Should().EndWith("…", "a cut nobody can see is a lie about what was said");
    }

    [Fact]
    public void ALongMessageToSlack_IsLeftAlone_BecauseSlackTakesIt() =>
        _Field(ChatRunner.Body(new string('x', 3000), discord: false), "text").Should().HaveLength(3000);

    private static string _Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetString()!;
}
