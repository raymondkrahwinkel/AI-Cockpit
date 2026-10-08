using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.App.ViewTests;

public sealed class ServerGroupHeaderTimeTests
{
    [Fact]
    public void HeaderDetail_UpdatesUptimeWhenTheClockTicks()
    {
        var now = DateTimeOffset.UnixEpoch;
        var clock = new Ac1321TakeoverStateTests.FakeTimeProvider(now);
        var key = new RemoteServerKey("admin", "admin", false, null, "1.0.0", now);
        var server = ServerGroupScene.StandIn("server", new RemoteServerState(true, 20, key)).Servers.Single();
        var group = new ServerGroupViewModel(server, time: clock);
        var headerDetail = "";
        group.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ServerGroupViewModel.HeaderDetail))
            {
                headerDetail = group.HeaderDetail;
            }
        };

        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal("v1.0.0 · up 0 h 1 min", headerDetail);
    }
}
