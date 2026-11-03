using Cockpit.Plugin.Kind.Settings;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.Kind.Tests;

// KindClusterManager against a fake CliRunner (AC-179 criteria 1, 3-6, 10) — no real kind/docker needed; the real
// end-to-end run lives in KindClusterLiveTests.
public class KindClusterManagerTests
{
    private const string OwnerPane = "pane-1";

    [Fact]
    public async Task DeleteAsync_UnregisteredName_RefusesWithoutRunningKind()
    {
        var (manager, _, cli, _) = _Manager();

        var (ok, error) = await manager.DeleteAsync("not-a-registered-cluster", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("No kind cluster named", error);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ReconcileAsync_PinnedRecordWithDeadOwner_IsKept()
    {
        var (manager, settings, _, _) = _Manager();
        await manager.CreateAsync("pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [settings.KindClusters.Single() with { IsPinned = true }];

        await manager.ReconcileAsync(liveSessionIds: [], CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task ReconcileAsync_NeverInvokesKindForAnUnregisteredName()
    {
        var (manager, settings, cli, _) = _Manager();
        await manager.CreateAsync("registered", OwnerPane, CancellationToken.None);
        cli.Calls.Clear();

        await manager.ReconcileAsync(liveSessionIds: [], CancellationToken.None);

        // Criterion 10: the sweep only ever iterates settings.KindClusters, so the one delete call it makes can
        // only ever name a registered cluster — proven here by asserting the exact argv, not just the count.
        var deleteCall = Assert.Single(cli.Calls);
        Assert.Contains("registered", deleteCall.Arguments);
    }

    [Fact]
    public async Task SweepExpiredAsync_PastMaxLifetimeButPinned_IsKept()
    {
        var (manager, settings, _, _) = _Manager();
        await manager.CreateAsync("stale-but-pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [settings.KindClusters.Single() with { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(5), IsPinned = true }];
        settings.KindClusterMaxLifetime = TimeSpan.FromHours(4);

        await manager.SweepExpiredAsync(CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task StopAllAsync_DeletesEveryNonPinnedCluster_ButKeepsPinnedOnes()
    {
        var (manager, settings, _, _) = _Manager();
        await manager.CreateAsync("to-stop", OwnerPane, CancellationToken.None);
        await manager.CreateAsync("pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [.. settings.KindClusters.Select(record => record.Name == "pinned" ? record with { IsPinned = true } : record)];

        await manager.StopAllAsync(CancellationToken.None);

        Assert.Equal("pinned", Assert.Single(settings.KindClusters).Name);
    }

    // AC-1349 (AC-1347 decision 2B): the operator's default for clusters this plugin makes rides along on the
    // register intent by name, so a fresh kind cluster lands in the Kubernetes plugin already in that mode.
    [Fact]
    public async Task CreateAsync_SendsTheConfiguredConsentModeOnTheRegisterIntent()
    {
        var (manager, settings, _, host) = _Manager();
        settings.ConsentModeForNewClusters = KindConsentMode.ReadFree;

        await manager.CreateAsync("cockpit-ac1349", OwnerPane, CancellationToken.None);

        await host.Received(1).SendIntent("kubernetes", "cluster.register", Arg.Is<IReadOnlyDictionary<string, string>>(
            data => data["consentMode"] == "ReadFree"));
    }

    // AC-1083's whole point: the Kubernetes plugin is optional. Without it the cluster still comes up and the
    // answer carries the kubeconfig and context to reach it by hand.

    // The default host has the Kubernetes plugin installed and answering (AC-1083); the test that proves the
    // degradation builds its own manager without one.
    private static (KindClusterManager Manager, KindSettings Settings, FakeCliRunner Cli, ICockpitHost Host) _Manager()
    {
        var settings = new KindSettings(new FakePluginStorage());
        var cli = new FakeCliRunner();
        var host = Substitute.For<ICockpitHost>();
        host.CanSendIntent("kubernetes", Arg.Any<string>()).Returns(true);
        host.SendIntent("kubernetes", Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["notice"] = string.Empty }));
        var directory = Directory.CreateTempSubdirectory("ac179-kind-tests").FullName;
        var manager = new KindClusterManager(settings, cli, new KindRuntime(cli), "kind", directory, host);
        return (manager, settings, cli, host);
    }
}
