using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Assistant;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.HeavyRuns;
using Cockpit.Infrastructure.Worktrees;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Diagnostics;

internal sealed class DockerStorageMaintenance(
    IMachineResources resources,
    IDockerCli docker,
    IDockerStoragePlatform platform,
    IElevatedCommandRunner elevated,
    HeavyRunQueue queue,
    IAgentMessageInbox inbox,
    ILiveSessionRegistry live,
    ILogger<DockerStorageMaintenance> logger,
    IToastService? toasts = null) : IDockerStorageMaintenance, ISingletonService, IAsyncDisposable
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource> _acknowledgements = new(StringComparer.Ordinal);
    private readonly Queue<string> _history = new();
    private ITimer? _timer;
    private Task _tick = Task.CompletedTask;
    private DateTimeOffset _lastCleanup;
    private DateTimeOffset _lastOffer;
    private string? _pauseId;
    private DockerDataDisk? _disk;
    private DockerStorageUsage? _usage;
    private int _ticking;
    private string _summary = "Docker storage has not been sampled.";
    internal Task Compaction { get; private set; } = Task.CompletedTask;

    public string Summary => Volatile.Read(ref _summary);
    public IReadOnlyList<string> History { get { lock (_gate) { return _history.ToArray(); } } }
    public bool CanCompact => platform.IsWindows && elevated.IsSupported && _disk is { } disk && _usage is { UsedBytes: > 0 } usage
        && disk.AllocatedBytes > 2d * usage.UsedBytes && resources.Disks.Any(drive => _OnDrive(disk.Path, drive.Name)
            && (drive.Pressure >= DiskPressure.Elevated || disk.AllocatedBytes - usage.UsedBytes > drive.TotalBytes * 0.1));
    public event Action? Changed;

    public void Start() => _timer ??= TimeProvider.System.CreateTimer(_ =>
    {
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) == 0)
        {
            _tick = _TickAsync();
        }
    }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));

    internal bool AcknowledgePause(string paneId, string pauseId)
    {
        lock (_gate)
        {
            return _pauseId == pauseId && _acknowledgements.TryGetValue(paneId, out var acknowledgement) && acknowledgement.TrySetResult();
        }
    }

    internal async Task SampleAsync(CancellationToken cancellationToken)
    {
        _usage = null;
        _disk = null;
        var result = await docker.RunAsync(["system", "df", "--format", "json"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Volatile.Write(ref _summary, "Docker is unavailable; disk monitoring continues.");
            Changed?.Invoke();
            return;
        }

        var usage = DockerStorageUsage.Parse(result.StandardOutput);
        DockerDataDisk? disk = null;
        try
        {
            disk = platform.ReadDataDisk();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            logger.LogWarning(exception, "Reading the Docker data disk failed; Docker object monitoring continues.");
        }
        _usage = usage;
        _disk = disk;
        Volatile.Write(ref _summary, usage.Description + (disk is null ? string.Empty
            : $"; VHDX {disk.Path}: {disk.Length / 1e9:0.0} GB logical, {disk.AllocatedBytes / 1e9:0.0} GB allocated; factor {disk.AllocatedBytes / (double)Math.Max(1, usage.UsedBytes):0.0} (estimate)"));
        Changed?.Invoke();
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        if (!await _operation.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await SampleAsync(linked.Token).ConfigureAwait(false);
            if (_usage is null)
            {
                return;
            }

            var pressure = resources.Disks.Select(drive => drive.Pressure).DefaultIfEmpty().Max();
            var hours = pressure switch { DiskPressure.Critical => 2, DiskPressure.Elevated => 24, _ => 72 };
            var builder = await _DockerAsync(["builder", "prune", "-f", "--filter", $"until={hours}h"], linked.Token).ConfigureAwait(false);
            var images = await _DockerAsync(["image", "prune", "-a", "-f", "--filter", $"until={hours}h"], linked.Token).ConfigureAwait(false);
            _lastCleanup = DateTimeOffset.UtcNow;
            await SampleAsync(linked.Token).ConfigureAwait(false);
            _Report($"Docker cleanup (older than {hours}h): build cache {_Reclaimed(builder)}, images {_Reclaimed(images)}. "
                + string.Join("; ", resources.Disks.Select(drive => $"{drive.Name} {resources.FreeDiskBytes(drive.Name) / 1e9:0.0} GB free")));
        }
        catch (Exception exception)
        {
            _Report("Docker cleanup failed: " + exception.Message, ToastSeverity.Warning);
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task RequestCompactionAsync(CancellationToken cancellationToken = default)
    {
        if (!platform.IsWindows)
        {
            _Report("VHDX compaction skipped on Linux/non-Windows; Docker Engine cleanup releases host filesystem space.");
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        if (!await _operation.WaitAsync(0, linked.Token).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await SampleAsync(linked.Token).ConfigureAwait(false);
            if (!CanCompact || toasts is null)
            {
                return;
            }

            var disk = _disk!;
            var distros = await platform.RunningDistrosAsync(linked.Token).ConfigureAwait(false);
            var plan = elevated.Plan(CompactionCommand(disk.Path));
            var clicked = 0;
            toasts.Show($"Compact {disk.Path}? {disk.AllocatedBytes / 1e9:0.0} GB allocated; Docker objects {_usage!.UsedBytes / 1e9:0.0} GB (estimate). Docker and all WSL distributions stop for about 5 minutes. Running WSL: {string.Join(", ", distros.DefaultIfEmpty("none"))}. One Windows UAC prompt follows. Command: {plan.CommandLine}",
                ToastSeverity.Warning,
                [new ToastAction("Compact", () =>
                {
                    if (Interlocked.Exchange(ref clicked, 1) == 0)
                    {
                        Compaction = _CompactAsync(disk, distros, plan);
                    }
                }), new ToastAction("Cancel", () => Interlocked.Exchange(ref clicked, 1))], true);
        }
        catch (Exception exception)
        {
            _Report("Could not offer compaction: " + exception.Message, ToastSeverity.Warning);
        }
        finally
        {
            _operation.Release();
        }
    }

    internal static string CompactionCommand(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Invalid VHDX path.", nameof(path));
        }

        var literal = path.Replace("'", "''", StringComparison.Ordinal);
        return $"@('select vdisk file=\"{literal}\"','attach vdisk readonly','compact vdisk','detach vdisk') | & \"$env:SystemRoot\\System32\\diskpart.exe\"; if ($LASTEXITCODE -ne 0) {{ throw 'diskpart failed' }}";
    }

    private async Task _CompactAsync(DockerDataDisk approvedDisk, IReadOnlyList<string> approvedDistros, ElevatedCommandPlan plan)
    {
        if (!await _operation.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        var restart = false;
        var participants = live.LiveSessionIds.ToArray();
        try
        {
            _stopping.Token.ThrowIfCancellationRequested();
            await using var reservation = queue.ReserveMaintenance(Environment.CurrentDirectory);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, reservation.Revoked);
            bounded.CancelAfter(TimeSpan.FromMinutes(5));
            await queue.WaitForIdleAsync(reservation, bounded.Token).ConfigureAwait(false);
            var pauseId = Guid.NewGuid().ToString("N");
            Task[] acknowledgements;
            lock (_gate)
            {
                _pauseId = pauseId;
                foreach (var pane in participants)
                {
                    _acknowledgements[pane] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                acknowledgements = _acknowledgements.Values.Select(source => source.Task).ToArray();
            }

            foreach (var pane in participants)
            {
                var delivery = inbox.Deliver("cockpit-storage", pane, "storage pause", $"Operator requested Docker/WSL compaction. Finish Docker work and pause it; acknowledge with cockpit-agents acknowledge_storage_pause, pauseId={pauseId}. Wait for storage resume before using Docker again.");
                if (delivery.Outcome == AgentMessageDeliveryOutcome.RecipientInboxFull)
                {
                    throw new InvalidOperationException($"Could not notify {pane}; compaction canceled.");
                }
            }

            await Task.WhenAll(acknowledgements).WaitAsync(bounded.Token).ConfigureAwait(false);
            bounded.CancelAfter(Timeout.InfiniteTimeSpan);
            if (!live.LiveSessionIds.SetEquals(participants))
            {
                throw new InvalidOperationException("The session roster changed; request compaction again.");
            }

            var distros = await platform.RunningDistrosAsync(bounded.Token).ConfigureAwait(false);
            if (!distros.SequenceEqual(approvedDistros, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Running WSL distributions changed; request compaction again.");
            }

            await SampleAsync(bounded.Token).ConfigureAwait(false);
            if (!CanCompact || _disk?.Path != approvedDisk.Path)
            {
                throw new InvalidOperationException("The storage condition changed; compaction canceled.");
            }

            bounded.Token.ThrowIfCancellationRequested();
            restart = true;
            try
            {
                await _DockerAsync(["desktop", "stop"], bounded.Token).ConfigureAwait(false);
                await platform.ShutdownAsync(bounded.Token).ConfigureAwait(false);
                var result = await elevated.RunAsync(plan, Environment.CurrentDirectory, TimeSpan.FromMinutes(10), bounded.Token).ConfigureAwait(false);
                if (result.Outcome != ElevatedCommandOutcome.Completed || result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Compaction {result.Outcome}: {result.Error ?? result.Output}");
                }
            }
            finally
            {
                await _RestartDockerAsync().ConfigureAwait(false);
                restart = false;
            }

            await SampleAsync(bounded.Token).ConfigureAwait(false);
            _Report($"Compaction finished: {(approvedDisk.AllocatedBytes - (_disk?.AllocatedBytes ?? approvedDisk.AllocatedBytes)) / 1e9:0.0} GB returned to the host.");
        }
        catch (Exception exception)
        {
            _Report("Compaction stopped: " + exception.Message + (restart ? " Docker restart failed; check Docker Desktop." : string.Empty), ToastSeverity.Warning);
        }
        finally
        {
            lock (_gate)
            {
                _pauseId = null;
                _acknowledgements.Clear();
            }

            foreach (var pane in participants)
            {
                inbox.Deliver("cockpit-storage", pane, "storage resume", restart ? "Maintenance ended; Docker recovery failed. Check Docker Desktop before resuming Docker work." : "Maintenance ended; the heavy-run queue is released and Docker work can resume.");
            }

            _operation.Release();
        }
    }

    private async Task _RestartDockerAsync()
    {
        using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await _DockerAsync(["desktop", "start"], recovery.Token).ConfigureAwait(false);
        while (true)
        {
            var result = await docker.RunAsync(["version"], recovery.Token).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), recovery.Token).ConfigureAwait(false);
        }
    }

    private async Task _TickAsync()
    {
        try
        {
            var pressure = resources.Disks.Select(drive => drive.Pressure).DefaultIfEmpty().Max();
            var now = DateTimeOffset.UtcNow;
            if (now - _lastCleanup >= (pressure >= DiskPressure.Elevated ? TimeSpan.FromMinutes(15) : TimeSpan.FromDays(1)))
            {
                await CleanupAsync(_stopping.Token).ConfigureAwait(false);
            }
            else if (await _operation.WaitAsync(0, _stopping.Token).ConfigureAwait(false))
            {
                try { await SampleAsync(_stopping.Token).ConfigureAwait(false); }
                finally { _operation.Release(); }
            }

            if (CanCompact && now - _lastOffer >= TimeSpan.FromDays(1))
            {
                _lastOffer = now;
                await RequestCompactionAsync(_stopping.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Storage maintenance sample failed; retrying next minute.");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private async Task<string> _DockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await docker.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput : throw new InvalidOperationException($"docker {string.Join(' ', arguments)} failed: {result.StandardError}");
    }

    private void _Report(string text, ToastSeverity severity = ToastSeverity.Information)
    {
        lock (_gate)
        {
            _history.Enqueue($"{DateTimeOffset.Now:g}: {text}");
            while (_history.Count > 10) { _history.Dequeue(); }
        }

        toasts?.Show(text, severity);
        inbox.Deliver("cockpit-storage", AssistantIdentity.PaneId, "storage maintenance", text);
        Changed?.Invoke();
    }

    private static string _Reclaimed(string output) => output.Split('\n').LastOrDefault(line => line.StartsWith("Total reclaimed space:", StringComparison.Ordinal)) ?? "completed; reclaimed bytes not reported";
    private static bool _OnDrive(string path, string name) => path.StartsWith(Path.EndsInDirectorySeparator(name) ? name : name + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_timer is not null) { await _timer.DisposeAsync().ConfigureAwait(false); }
        await _tick.ConfigureAwait(false);
        await _operation.WaitAsync().ConfigureAwait(false);
        _operation.Release();
    }
}
