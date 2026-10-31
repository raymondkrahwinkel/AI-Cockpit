using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Remote;
using NSubstitute;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public sealed class Ac1584ServerLatencyTests
{
    [Fact]
    public async Task AConnectedServerWithoutAProbeAnswer_ShowsNoAnswer()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var server = Substitute.For<IRemoteServer>();
            var servers = Substitute.For<IRemoteServers>();
            var state = new RemoteServerState(true, 24, null);
            server.Name.Returns("server");
            server.State.Returns(_ => state);
            servers.Servers.Returns([server]);
            var view = new NodeSessionsViewModel("server") { Url = "https://server.example" };

            view.FollowServer(servers);
            state = state with { LatencyMs = null };
            server.StateChanged += Raise.Event();
            await Dispatcher.UIThread.InvokeAsync(() => { });

            Assert.True(view.IsConnected);
            Assert.Contains("no answer", view.ConnectionDetail);
        });
    }
}
