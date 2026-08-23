namespace Cockpit.Plugin.Slack.Tests;

// AC-1049: the cap has to hold while reading, because a Content-Length is something the other side says and may
// not say at all.
public class SlackFileFetcherTests
{
    [Fact]
    public async Task ReadsAFileThatFitsTheCap()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5]);

        var bytes = await SlackFileFetcher.ReadCappedAsync(stream, cap: 1024);

        Assert.Equal([1, 2, 3, 4, 5], bytes);
    }

    [Fact]
    public async Task StopsOnAStreamThatRunsPastTheCap()
    {
        using var stream = new MemoryStream(new byte[4096]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => SlackFileFetcher.ReadCappedAsync(stream, cap: 1024));
    }

    [Fact]
    public async Task ReadsRightUpToTheCap()
    {
        using var stream = new MemoryStream(new byte[1024]);

        var bytes = await SlackFileFetcher.ReadCappedAsync(stream, cap: 1024);

        Assert.Equal(1024, bytes.Length);
    }
}
