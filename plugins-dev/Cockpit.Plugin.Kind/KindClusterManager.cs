using Cockpit.Plugin.Kind.Cli;
using Cockpit.Plugin.Kind.Settings;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.StatusBar;

namespace Cockpit.Plugin.Kind;

// Owns the kind-cluster lifecycle (AC-179): the registry (KindSettings.KindClusters, not the containers on disk)
// is the source of truth for cleanup, mirroring WorktreeManager.
internal sealed class KindClusterManager(
    KindSettings settings,
    ICliRunner runner,
    KindRuntime kindRuntime,
    string kindExecutablePath,
    string kubeconfigDirectory,
    ICockpitHost? host = null) : ISupervisedActivitySource
{
    // Cold node-image pull measured at 1.35 GB (AC-179 grooming) — helm's 2-minute test deadline is too tight here.
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);

    public string Label => "Kind clusters";

    public event Action? Changed;

    public async Task<(KindClusterRecord? Record, string? Error)> CreateAsync(string name, string ownerPaneId, CancellationToken cancellationToken)
    {
        if (settings.KindClusters.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
        {
            return (null, $"A kind cluster named \"{name}\" is already registered — use kind_list to check, or pick a different name.");
        }

        var runtimeStatus = await kindRuntime.DetectAsync(cancellationToken);
        if (!runtimeStatus.IsInstalled)
        {
            return (null, runtimeStatus.Message);
        }

        Directory.CreateDirectory(kubeconfigDirectory);
        var kubeconfigPath = Path.Combine(kubeconfigDirectory, $"{name}.kubeconfig");

        var result = await runner.RunAsync(KindCommand.Create(kindExecutablePath, name, kubeconfigPath), CreateTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return (null, KindFailure.Describe(result, kindExecutablePath));
        }

        var record = new KindClusterRecord(name, ownerPaneId, kubeconfigPath, DateTimeOffset.UtcNow);
        settings.KindClusters = [.. settings.KindClusters, record];
        Changed?.Invoke();
        return (record, await KubernetesClusterGate.RegisterAsync(host, name, kubeconfigPath));
    }

    public async Task<IReadOnlyList<KindClusterListEntry>> ListAsync(CancellationToken cancellationToken)
    {
        var runningNames = await _RunningClusterNamesAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return settings.KindClusters
            .Select(record => new KindClusterListEntry(
                record.Name,
                now - record.CreatedAt,
                record.OwnerPaneId,
                record.KubeconfigPath,
                record.IsPinned,
                runningNames.Contains(record.Name)))
            .ToList();
    }

    public async Task<(bool Ok, string? Error)> DeleteAsync(string name, CancellationToken cancellationToken)
    {
        // Criterion 10: a name not in the registry is never touched, however it is spelled or found.
        var record = settings.KindClusters.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (record is null)
        {
            return (false, $"No kind cluster named \"{name}\" is registered — use kind_list for the current names.");
        }

        var result = await runner.RunAsync(KindCommand.Delete(kindExecutablePath, record.Name, record.KubeconfigPath), DeleteTimeout, cancellationToken);
        if (!result.Succeeded)
        {
            return (false, KindFailure.Describe(result, kindExecutablePath));
        }

        settings.KindClusters = settings.KindClusters.Where(candidate => !string.Equals(candidate.Name, name, StringComparison.Ordinal)).ToList();
        await KubernetesClusterGate.UnregisterAsync(host, name);
        try
        {
            File.Delete(record.KubeconfigPath);
        }
        catch (Exception)
        {
            // Best-effort: the registry entry is already gone, which is what every other tool reads from.
        }

        Changed?.Invoke();
        return (true, null);
    }

    // Startup crash net (AC-179 criterion 8): orphan = owner pane not in the live set, never age — exactly
    // WorktreeManager.ReconcileAsync's rule. Pinned clusters are exempt regardless of their owner's liveness.
    public Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken) =>
        _DeleteMatchingAsync(record => !liveSessionIds.Contains(record.OwnerPaneId), cancellationToken);

    // TTL backstop (criterion 11), next to the sweep above rather than instead of it.
    public Task SweepExpiredAsync(CancellationToken cancellationToken) =>
        _DeleteMatchingAsync(record => DateTimeOffset.UtcNow - record.CreatedAt > settings.KindClusterMaxLifetime, cancellationToken);

    // Shutdown teardown (criterion 9) — called from KindPlugin.Dispose(), bounded and best-effort there.
    public Task StopAllAsync(CancellationToken cancellationToken) => _DeleteMatchingAsync(_ => true, cancellationToken);

    public IReadOnlyList<SupervisedActivity> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return settings.KindClusters
            .Select(record => new SupervisedActivity(
                record.Name,
                $"{record.Name}  {_FormatAge(now - record.CreatedAt)}",
                [
                    new ActivityDetail("owner", record.OwnerPaneId),
                    new ActivityDetail("pinned", record.IsPinned ? "yes" : "no"),
                ],
                () => DeleteAsync(record.Name, CancellationToken.None)))
            .ToList();
    }

    // Pin exemption lives here once, so the orphan sweep, the TTL sweep and shutdown teardown cannot drift apart on
    // it, and criterion 10 (only registry-known clusters) holds structurally — this only ever iterates the registry.
    private async Task _DeleteMatchingAsync(Func<KindClusterRecord, bool> predicate, CancellationToken cancellationToken)
    {
        var matches = settings.KindClusters.Where(record => !record.IsPinned && predicate(record)).ToList();
        foreach (var record in matches)
        {
            try
            {
                await DeleteAsync(record.Name, cancellationToken);
            }
            catch (Exception)
            {
                // One record that will not delete must not abort the sweep for the rest — the next sweep retries it.
            }
        }
    }

    // Pitfall from the ticket: an empty `kind get clusters` is not proof of absence. This is used only for the
    // IsRunning flag on an already-registered entry, never to decide what to sweep — the registry stays the source
    // of truth for existence either way.
    private async Task<HashSet<string>> _RunningClusterNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(KindCommand.GetClusters(kindExecutablePath), ListTimeout, cancellationToken);
            if (!result.Succeeded)
            {
                return [];
            }

            return result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string _FormatAge(TimeSpan age) =>
        age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes}m" : $"{(int)age.TotalHours}h{age.Minutes}m";
}
