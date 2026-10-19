using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Backup;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.Backup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Backup;

// AC-1502: backs the cockpit up on a schedule, in the backend so a headless server does it too, then prunes the
// target down to `Keep`. A failure is never swallowed: it is logged, shown, kept on disk for the next start, and the
// next tick tries again because only a success moves the clock. Core on purpose: it works without any plugin (AC-885).
public sealed class ScheduledBackup : ISingletonService, IDisposable
{
    // How often the schedule is looked at, not how often it backs up: a due backup waits at most this long.
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(15);

    private readonly IBackupService _backups;
    private readonly IBackupScheduleStore _store;
    private readonly IToastService? _toasts;
    private readonly ILogger<ScheduledBackup> _logger;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();

    private ITimer? _timer;
    private Task _lastTick = Task.CompletedTask;
    private string? _lastReported;
    private int _running;
    private bool _disposed;

    public ScheduledBackup(
        IBackupService backups,
        IBackupScheduleStore store,
        IToastService? toasts = null,
        ILogger<ScheduledBackup>? logger = null)
        : this(backups, store, toasts, logger, TimeProvider.System)
    {
    }

    // Test seam: a controllable clock, so a due backup is provable without waiting a day for it.
    internal ScheduledBackup(
        IBackupService backups,
        IBackupScheduleStore store,
        IToastService? toasts,
        ILogger<ScheduledBackup>? logger,
        TimeProvider time)
    {
        _backups = backups;
        _store = store;
        _toasts = toasts;
        _logger = logger ?? NullLogger<ScheduledBackup>.Instance;
        _time = time;
    }

    // Where a schedule's backups go. Replaced by the tests; the default knows the local folder and nothing else
    // until a plugin can register a target (AC-1508).
    internal Func<BackupSchedule, IBackupTarget?> TargetFor { get; set; } = _LocalFolderFor;

    // The tick in flight, for the tests to await.
    internal Task Ticking => _lastTick;

    // Says what the last run left unsaid, then starts watching the clock. Idempotent.
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = _time.CreateTimer(_ => _lastTick = _TickAsync(), null, Tick, Tick);
        _lastTick = _StartAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _stopping.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    // One look at the schedule. Public because the tests drive it directly, like the other backend planners.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var schedule = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!schedule.Enabled)
            {
                return;
            }

            var state = await _store.LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            // Only a success moves the clock, so a failed backup is due again on the very next tick.
            if (state.LastSuccess is { } last && now - last < schedule.Interval)
            {
                return;
            }

            // A manual backup or a restore is running: this one stands aside and the next tick looks again.
            if (_backups.IsBusy)
            {
                return;
            }

            await _BackUpAsync(schedule, now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task _BackUpAsync(BackupSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var target = TargetFor(schedule) ?? throw new InvalidOperationException($"there is no backup target '{schedule.TargetId}'.");
            var written = string.Empty;

            await _backups.WriteAsync(
                async (archive, token) => written = await target.WriteAsync($"cockpit-backup-{now:yyyyMMdd-HHmmss}.zip", archive, token).ConfigureAwait(false),
                new BackupOptions(),
                cancellationToken).ConfigureAwait(false);

            // A backup that is made but not pruned is still a backup: it counts, and the prune says what it could not do.
            var note = await _PruneAsync(target, written, schedule.KeepAtLeastOne, cancellationToken).ConfigureAwait(false);
            await _store.SaveStateAsync(new BackupScheduleState(now, note), cancellationToken).ConfigureAwait(false);
            _Report(note);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The scheduled backup failed; the next check will try again.");

            var message = $"The scheduled backup failed: {exception.Message} It will be tried again.";
            var state = await _store.LoadStateAsync(CancellationToken.None).ConfigureAwait(false);
            await _store.SaveStateAsync(state with { LastFailure = message }, CancellationToken.None).ConfigureAwait(false);
            _Report(message);
        }
    }

    // Keeps the backup just written plus the newest others, up to `keep`. By CreatedAt, never by id: a name is not an
    // age, and this is the one place a wrong order deletes the wrong backup. Null when nothing needed saying.
    private static async Task<string?> _PruneAsync(IBackupTarget target, string written, int keep, CancellationToken cancellationToken)
    {
        var entries = await target.ListAsync(cancellationToken).ConfigureAwait(false);
        var stale = entries
            .Where(entry => entry.Id != written)
            .OrderByDescending(entry => entry.Id, StringComparer.Ordinal)
            .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
            .Skip(keep - 1)
            .ToList();

        foreach (var entry in stale)
        {
            if (!await target.DeleteAsync(entry.Id, cancellationToken).ConfigureAwait(false))
            {
                return "The backup was made, but this target cannot remove old backups, so all of them are kept.";
            }
        }

        return null;
    }

    private async Task _StartAsync()
    {
        try
        {
            var state = await _store.LoadStateAsync(_stopping.Token).ConfigureAwait(false);
            _Report(state.LastFailure);
            await RunOnceAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _LogTickFailure(exception);
        }
    }

    private async Task _TickAsync()
    {
        try
        {
            await RunOnceAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _LogTickFailure(exception);
        }
    }

    private void _LogTickFailure(Exception exception)
    {
        if (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "A scheduled backup check failed; the next one will try again.");
        }
    }

    // A message is shown once however many ticks repeat it; a clean run forgets it, so the next failure shows again.
    private void _Report(string? message)
    {
        if (message is null)
        {
            _lastReported = null;
            return;
        }

        if (message != _lastReported)
        {
            _lastReported = message;
            _toasts?.Show(message, ToastSeverity.Error);
        }
    }

    private static IBackupTarget? _LocalFolderFor(BackupSchedule schedule)
    {
        if (schedule.TargetId != BackupSchedule.LocalFolderTargetId)
        {
            return null;
        }

        var folder = schedule.Folder ?? Path.Combine(CockpitConfigPath.Root, "backups");
        Directory.CreateDirectory(folder);

        return new LocalFolderBackupTarget(folder, BackupService.StagingIn(CockpitConfigPath.Root));
    }
}
