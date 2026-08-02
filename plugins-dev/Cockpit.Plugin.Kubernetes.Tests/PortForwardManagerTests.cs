using k8s;
using Cockpit.Plugin.Kubernetes.Cluster;
using NSubstitute;

namespace Cockpit.Plugin.Kubernetes.Tests;

// The port-forward manager's lifecycle without a cluster: opening a tunnel binds a local listener and lists it as a
// supervised activity (source/target/cluster), and killing it — directly or through the status-bar activity's
// StopAsync — removes it and fires Changed. The pod WebSocket only opens on an incoming connection, so these
// exercise the manager's bookkeeping and the operator-facing Kill wiring, not a live apiserver.
public class PortForwardManagerTests
{
    [Fact]
    public async Task Start_ListsTheTunnelWithItsDetails_ThenStopRemovesIt()
    {
        var manager = new PortForwardManager();
        var changed = 0;
        manager.Changed += () => changed++;

        var tunnel = manager.Start(Substitute.For<IKubernetes>(), "prod", "default", "nginx", 80, requestedLocalPort: 0, TimeSpan.FromMinutes(1));

        Assert.True(tunnel.LocalPort > 0, "port 0 means the OS picked a free one");
        var activities = manager.Snapshot();
        Assert.Single(activities);
        Assert.Contains(activities[0].Details, detail => detail.Label == "cluster" && detail.Value == "prod");
        Assert.Contains(activities[0].Details, detail => detail.Label == "namespace" && detail.Value == "default");
        Assert.True(changed > 0, "opening a tunnel changes the set");

        await manager.StopAsync(tunnel.Id);

        Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public async Task SupervisedActivityStop_IsTheOperatorKill_AndRemovesTheTunnel()
    {
        var manager = new PortForwardManager();
        manager.Start(Substitute.For<IKubernetes>(), "prod", "default", "nginx", 80, requestedLocalPort: 0, TimeSpan.FromMinutes(1));

        var activity = manager.Snapshot().Single();
        await activity.StopAsync();

        Assert.Empty(manager.Snapshot());
    }

    [Fact]
    public async Task StopAllAsync_ClosesEveryTunnel()
    {
        var manager = new PortForwardManager();
        var client = Substitute.For<IKubernetes>();
        manager.Start(client, "prod", "default", "a", 80, requestedLocalPort: 0, TimeSpan.FromMinutes(1));
        manager.Start(client, "prod", "default", "b", 81, requestedLocalPort: 0, TimeSpan.FromMinutes(1));

        Assert.Equal(2, System.Linq.Enumerable.Count(manager.Snapshot()));
        await manager.StopAllAsync();

        Assert.Empty(manager.Snapshot());
    }
}
