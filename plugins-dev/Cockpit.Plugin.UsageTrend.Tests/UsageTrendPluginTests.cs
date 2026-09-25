using System.Text.Json;
using Cockpit.Plugin.UsageTrend.Contracts;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.UsageTrend.Tests;

// AC-1395: the widget's cache-backed history moved behind the plugin's own channel (ICockpitUiHost has no
// Cache). This is the seam that moved — the debounce/prune rules themselves are UsageTrendHistoryTests'.
public class UsageTrendPluginTests
{
    [Fact]
    public async Task Initialize_AppendThenGet_RoundTripsTheSampleThroughTheBackendsCache()
    {
        var channel = new InProcessChannel();
        var host = Substitute.For<ICockpitHost>();
        host.Cache.Returns(new InMemoryPluginCache());
        host.Channel.Returns(channel);

        using var plugin = new UsageTrendPlugin();
        plugin.Initialize(host);

        var sample = new UsageTrendSample(DateTimeOffset.UtcNow, "Default", 20, 30, 40);
        var appendPayload = JsonSerializer.SerializeToElement(new UsageTrendAppendRequest("instance-1", sample), UsageTrendChannel.Json);
        var appendAnswer = await channel.InvokeAsync(UsageTrendChannel.Append, appendPayload);
        Assert.Single(appendAnswer.Deserialize<List<UsageTrendSample>>(UsageTrendChannel.Json) ?? []);

        var getPayload = JsonSerializer.SerializeToElement(new UsageTrendHistoryRequest("instance-1"), UsageTrendChannel.Json);
        var getAnswer = await channel.InvokeAsync(UsageTrendChannel.Get, getPayload);
        var loaded = getAnswer.Deserialize<List<UsageTrendSample>>(UsageTrendChannel.Json);

        var only = Assert.Single(loaded ?? []);
        Assert.Equal(sample.ProfileLabel, only.ProfileLabel);
        Assert.Equal(sample.ContextPercent, only.ContextPercent);
    }
}
