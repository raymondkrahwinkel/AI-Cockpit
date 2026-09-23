namespace Cockpit.Plugin.Discord.Tests;

// AC-1360 / AC-625 §5: direct messages by default, one channel only when it is opted into, never another bot. Who
// sent it is the bridge's check (DiscordChannelBridgeImageTests' stranger rows); this is where it came from.
public class DiscordInboundFilterTests
{
    private const ulong _OptIn = 42;

    [Theory]
    [InlineData(false, true, 7ul, 0ul, true)]
    [InlineData(false, false, _OptIn, 0ul, false)]
    [InlineData(false, false, _OptIn, _OptIn, true)]
    [InlineData(false, false, 7ul, _OptIn, false)]
    [InlineData(false, true, 7ul, _OptIn, false)]
    [InlineData(true, true, 7ul, 0ul, false)]
    [InlineData(true, false, _OptIn, _OptIn, false)]
    public void AMessageCounts_OnlyInADirectMessage_OrInTheOneChannelOptedInto(
        bool fromBot, bool isDirectMessage, ulong channelId, ulong optInChannelId, bool accepted) =>
        Assert.Equal(accepted, DiscordInboundFilter.Accepts(fromBot, isDirectMessage, channelId, optInChannelId));
}
