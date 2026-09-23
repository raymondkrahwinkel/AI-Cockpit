using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Discord.Tests;

// AC-1360 criterion 3: a long message arrives whole, in parts cut at a line break, and only ever at the one allowed
// account — a step with no single account configured fails visibly and sends nothing anywhere.
public class DiscordSendDmStepTests
{
    // 45 lines of 100 characters: 4500 in all, so 2000 + 2000 + 500 when cut at a line break.
    private static readonly string _Long = string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 45));

    public static TheoryData<AssistantChannelAccess?, int, string, bool> Cases => new()
    {
        { AssistantChannelAccess.ForSingleUser("111").Access, 3, _Long, false },
        { AssistantChannelAccess.ForUsers(["111", "222"], warningAcknowledged: true).Access, 0, string.Empty, true },
        { AssistantChannelAccess.ForEveryone(AssistantChannelAccess.EveryoneConfirmationPhrase).Access, 0, string.Empty, true },
        { null, 0, string.Empty, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ALongMessage_ArrivesWholeInParts_AndOnlyAtTheOneAllowedAccount(
        AssistantChannelAccess? access, int expectedParts, string expectedDelivered, bool fails)
    {
        var sent = new List<(string UserId, string Part)>();
        var step = new DiscordSendDmStep(
            () => access,
            (userId, parts, _) =>
            {
                sent.AddRange(parts.Select(part => (userId, part)));
                return Task.CompletedTask;
            });
        var context = new WorkflowStepContext(new Dictionary<string, string> { [DiscordSendDmStep.MessageParameter] = _Long }, []);

        var error = await Record.ExceptionAsync(() => step.RunAsync(context, CancellationToken.None));

        Assert.Equal(fails, error is InvalidOperationException);
        Assert.Equal(expectedParts, sent.Count);
        Assert.Equal(expectedDelivered, string.Concat(sent.Select(entry => entry.Part)));
        Assert.All(sent, entry => Assert.Equal("111", entry.UserId));
        Assert.All(sent, entry => Assert.True(entry.Part.Length <= DiscordSendDmStep.DiscordLimit && entry.Part.EndsWith('\n')));
    }
}
