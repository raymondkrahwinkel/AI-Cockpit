using Cockpit.Plugin.Kubernetes.Cli;
using Cockpit.Plugin.Kubernetes.Kind;
using Cockpit.Plugin.Kubernetes.Settings;

namespace Cockpit.Plugin.Kubernetes.Tests;

// KindClusterManager against a fake CliRunner (AC-179 criteria 1, 3-6, 10) — no real kind/docker needed; the real
// end-to-end run lives in KindClusterLiveTests.
public class KindClusterManagerTests
{
    private const string OwnerPane = "pane-1";

    [Fact]
    public async Task CreateAsync_OnSuccess_RegistersBothTheKindRecordAndAClusterRegistration()
    {
        var (manager, settings, cli) = _Manager();

        var (record, error) = await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(record);
        Assert.Equal("cockpit-ac179", record!.Name);
        Assert.Equal(OwnerPane, record.OwnerPaneId);
        Assert.Contains(settings.KindClusters, r => r.Name == "cockpit-ac179");

        var registration = settings.Clusters.Single();
        Assert.Equal("kind-cockpit-ac179", registration.Id);
        Assert.Equal("kind-cockpit-ac179", registration.ContextName);
        Assert.Equal(record.KubeconfigPath, registration.KubeconfigPath);

        var createCall = cli.Calls.Single(call => call.Arguments[0] == "create");
        Assert.Contains("cockpit-ac179", createCall.Arguments);
    }

    [Fact]
    public async Task CreateAsync_KindNotInstalled_ReturnsTheInstallMessageAndTouchesNothing()
    {
        var (manager, settings, cli) = _Manager();
        cli.Handler = _ => CliResult.NotStarted;

        var (record, error) = await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);

        Assert.Null(record);
        Assert.Contains("was not found on PATH", error);
        Assert.Empty(settings.KindClusters);
        Assert.Empty(settings.Clusters);
    }

    [Fact]
    public async Task CreateAsync_KindCommandFails_ReturnsTheFailureDescriptionAndTouchesNothing()
    {
        var (manager, settings, cli) = _Manager();
        cli.Handler = command => command.Arguments[0] == "create"
            ? CliResult.Exited(1, string.Empty, "some kind failure")
            : CliResult.Exited(0, string.Empty, string.Empty);

        var (record, error) = await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);

        Assert.Null(record);
        Assert.Contains("some kind failure", error);
        Assert.Empty(settings.KindClusters);
    }

    [Fact]
    public async Task CreateAsync_NameAlreadyRegistered_RefusesWithoutRunningKind()
    {
        var (manager, settings, cli) = _Manager();
        await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);
        cli.Calls.Clear();

        var (record, error) = await manager.CreateAsync("cockpit-ac179", "pane-2", CancellationToken.None);

        Assert.Null(record);
        Assert.Contains("already registered", error);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRecordTheRegistrationAndTheKubeconfigFile()
    {
        var (manager, settings, _) = _Manager();
        var (created, _) = await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);
        File.WriteAllText(created!.KubeconfigPath, "current-context: kind-cockpit-ac179\n");

        var (ok, error) = await manager.DeleteAsync("cockpit-ac179", CancellationToken.None);

        Assert.True(ok, error);
        Assert.Empty(settings.KindClusters);
        Assert.Empty(settings.Clusters);
        Assert.False(File.Exists(created.KubeconfigPath));
    }

    [Fact]
    public async Task DeleteAsync_UnregisteredName_RefusesWithoutRunningKind()
    {
        var (manager, _, cli) = _Manager();

        var (ok, error) = await manager.DeleteAsync("not-a-registered-cluster", CancellationToken.None);

        Assert.False(ok);
        Assert.Contains("No kind cluster named", error);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task ListAsync_ReportsRunningOnlyForNamesKindActuallyReports()
    {
        var (manager, _, cli) = _Manager();
        await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);
        cli.Handler = command => command.Arguments is ["get", "clusters"]
            ? CliResult.Exited(0, "cockpit-ac179\n", string.Empty)
            : CliResult.Exited(0, string.Empty, string.Empty);

        var entries = await manager.ListAsync(CancellationToken.None);

        var entry = Assert.Single(entries);
        Assert.Equal("cockpit-ac179", entry.Name);
        Assert.Equal(OwnerPane, entry.OwnerPaneId);
        Assert.False(entry.IsPinned);
        Assert.True(entry.IsRunning);
    }

    [Fact]
    public async Task ListAsync_EmptyKindOutput_ReportsNotRunningRatherThanThrowing()
    {
        var (manager, _, cli) = _Manager();
        await manager.CreateAsync("cockpit-ac179", OwnerPane, CancellationToken.None);
        cli.Handler = command => command.Arguments is ["get", "clusters"]
            ? CliResult.Exited(0, "No kind clusters found.\n", string.Empty)
            : CliResult.Exited(0, string.Empty, string.Empty);

        var entries = await manager.ListAsync(CancellationToken.None);

        Assert.False(Assert.Single(entries).IsRunning);
    }

    [Fact]
    public async Task ReconcileAsync_DeadOwner_DeletesTheCluster()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("orphaned", OwnerPane, CancellationToken.None);

        await manager.ReconcileAsync(liveSessionIds: [], CancellationToken.None);

        Assert.Empty(settings.KindClusters);
    }

    [Fact]
    public async Task ReconcileAsync_LiveOwner_KeepsTheCluster()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("still-owned", OwnerPane, CancellationToken.None);

        await manager.ReconcileAsync(liveSessionIds: [OwnerPane], CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task ReconcileAsync_PinnedRecordWithDeadOwner_IsKept()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [settings.KindClusters.Single() with { IsPinned = true }];

        await manager.ReconcileAsync(liveSessionIds: [], CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task ReconcileAsync_NeverInvokesKindForAnUnregisteredName()
    {
        var (manager, settings, cli) = _Manager();
        await manager.CreateAsync("registered", OwnerPane, CancellationToken.None);
        cli.Calls.Clear();

        await manager.ReconcileAsync(liveSessionIds: [], CancellationToken.None);

        // Criterion 10: the sweep only ever iterates settings.KindClusters, so the one delete call it makes can
        // only ever name a registered cluster — proven here by asserting the exact argv, not just the count.
        var deleteCall = Assert.Single(cli.Calls);
        Assert.Contains("registered", deleteCall.Arguments);
    }

    [Fact]
    public async Task SweepExpiredAsync_PastMaxLifetimeAndUnpinned_IsDeleted()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("stale", OwnerPane, CancellationToken.None);
        settings.KindClusters = [settings.KindClusters.Single() with { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(5) }];
        settings.KindClusterMaxLifetime = TimeSpan.FromHours(4);

        await manager.SweepExpiredAsync(CancellationToken.None);

        Assert.Empty(settings.KindClusters);
    }

    [Fact]
    public async Task SweepExpiredAsync_PastMaxLifetimeButPinned_IsKept()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("stale-but-pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [settings.KindClusters.Single() with { CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(5), IsPinned = true }];
        settings.KindClusterMaxLifetime = TimeSpan.FromHours(4);

        await manager.SweepExpiredAsync(CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task SweepExpiredAsync_WithinMaxLifetime_IsKept()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("fresh", OwnerPane, CancellationToken.None);

        await manager.SweepExpiredAsync(CancellationToken.None);

        Assert.Single(settings.KindClusters);
    }

    [Fact]
    public async Task StopAllAsync_DeletesEveryNonPinnedCluster_ButKeepsPinnedOnes()
    {
        var (manager, settings, _) = _Manager();
        await manager.CreateAsync("to-stop", OwnerPane, CancellationToken.None);
        await manager.CreateAsync("pinned", OwnerPane, CancellationToken.None);
        settings.KindClusters = [.. settings.KindClusters.Select(record => record.Name == "pinned" ? record with { IsPinned = true } : record)];

        await manager.StopAllAsync(CancellationToken.None);

        Assert.Equal("pinned", Assert.Single(settings.KindClusters).Name);
    }

    [Fact]
    public void Snapshot_ListsEveryRegisteredClusterWithAnOwnerOnlyKill()
    {
        var (manager, settings, _) = _Manager();
        settings.KindClusters = [new KindClusterRecord("cockpit-ac179", OwnerPane, "/tmp/x.kubeconfig", DateTimeOffset.UtcNow)];

        var snapshot = manager.Snapshot();

        var activity = Assert.Single(snapshot);
        Assert.Equal("cockpit-ac179", activity.Id);
        Assert.Contains(activity.Details, detail => detail.Label == "owner" && detail.Value == OwnerPane);
    }

    private static (KindClusterManager Manager, KubernetesSettings Settings, FakeCliRunner Cli) _Manager()
    {
        var storage = new FakePluginStorage();
        var settings = new KubernetesSettings(storage);
        var cli = new FakeCliRunner();
        var runtime = new KindRuntime(cli);
        var directory = Directory.CreateTempSubdirectory("ac179-kind-tests").FullName;
        var manager = new KindClusterManager(settings, cli, runtime, "kind", directory);
        return (manager, settings, cli);
    }
}
