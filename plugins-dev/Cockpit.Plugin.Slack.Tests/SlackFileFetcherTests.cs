namespace Cockpit.Plugin.Slack.Tests;

// AC-1049: the cap has to hold while reading, because a Content-Length is something the other side says and may
// not say at all.
public class SlackFileFetcherTests
{
    [Fact]
    public async Task StopsOnAStreamThatRunsPastTheCap()
    {
        using var stream = new MemoryStream(new byte[4096]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SlackFileFetcher.ReadCappedAsync(stream, cap: 1024));
    }
}
