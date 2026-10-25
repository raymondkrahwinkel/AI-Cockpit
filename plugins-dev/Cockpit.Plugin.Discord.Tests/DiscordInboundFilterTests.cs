namespace Cockpit.Plugin.Discord.Tests;

// AC-1681: only direct messages from users reach the identity gate. Channels and other bots stop here.
public class DiscordInboundFilterTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public void AMessageCounts_OnlyWhenAUserSentItAsADirectMessage(
        bool fromBot, bool isDirectMessage, bool accepted) =>
        Assert.Equal(accepted, DiscordInboundFilter.Accepts(fromBot, isDirectMessage));
}
