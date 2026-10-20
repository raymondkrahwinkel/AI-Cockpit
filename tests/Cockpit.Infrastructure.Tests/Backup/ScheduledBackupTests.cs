using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Backup;
using Cockpit.Core.Provisioning;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Backup;
using Cockpit.Plugins.Abstractions.Backup;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-1502: the scheduled backup on a clock the test moves — it writes when the interval has passed, a failure is
/// shown and tried again, and retention removes the oldest backup by age and nothing else.
/// </summary>
public sealed class ScheduledBackupTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);

    private readonly string _home = Directory.CreateTempSubdirectory("cockpit-scheduled-backup").FullName;
    private readonly _ManualClock _clock = new(Start);
    private readonly _Toasts _toasts = new();
    private readonly _Target _target = new();
    private readonly _Service _service = new();
    private readonly BackupScheduleStore _store;
    private readonly ScheduledBackup _scheduled;

    public ScheduledBackupTests()
    {
        _store = new BackupScheduleStore(Path.Combine(_home, "cockpit.json"));
        _scheduled = new ScheduledBackup(_service, _store, _toasts, null, _clock) { TargetFor = _ => _target };
    }

    public void Dispose()
    {
        _scheduled.Dispose();
        Directory.Delete(_home, recursive: true);
    }

    /// <summary>
    /// Acceptance 1. Nothing is due at the start; one interval later the timer's tick writes exactly one backup. A
    /// planner that was never started has no timer to tick, so this goes red without <c>Start()</c>.
    /// </summary>
    [Fact]
    public async Task AnIntervalAfterTheLastBackup_TheTickWritesExactlyOne()
    {
        await _store.SaveAsync(new BackupSchedule(Enabled: true, IntervalHours: 24));
        await _store.SaveStateAsync(new BackupScheduleState(Start));

        _scheduled.Start();
        await _scheduled.Ticking;
        Assert.Empty(_target.Entries);

        _clock.Advance(TimeSpan.FromHours(24));
        _clock.FireTimer();
        await _scheduled.Ticking;

        Assert.Single(_target.Entries);
        Assert.Equal(_clock.GetUtcNow(), (await _store.LoadStateAsync()).LastSuccess);
    }

    /// <summary>
    /// Acceptance 2. A target that throws is shown once and remembered on disk; the next tick, with nothing moved
    /// but the clock, writes. Swallowing the exception leaves no toast and no stored failure, and this goes red.
    /// </summary>
    [Fact]
    public async Task ATargetThatFails_IsShownOnce_AndTheNextTickWrites()
    {
        await _store.SaveAsync(new BackupSchedule(Enabled: true, IntervalHours: 24));
        _target.Failure = new IOException("the disk is full");

        _scheduled.Start();
        await _scheduled.Ticking;
        _clock.Advance(TimeSpan.FromMinutes(15));
        _clock.FireTimer();
        await _scheduled.Ticking;

        Assert.Empty(_target.Entries);
        Assert.Single(_toasts.Messages);
        Assert.Contains("the disk is full", _toasts.Messages[0]);
        Assert.Contains("the disk is full", (await _store.LoadStateAsync()).LastFailure);

        _target.Failure = null;
        _clock.Advance(TimeSpan.FromMinutes(15));
        _clock.FireTimer();
        await _scheduled.Ticking;

        Assert.Single(_target.Entries);
        Assert.Null((await _store.LoadStateAsync()).LastFailure);
    }

    /// <summary>
    /// Acceptance 3 and 4, the data-loss one. With Keep 3 and a fourth backup the oldest by <c>CreatedAt</c> goes —
    /// the ids are laid out so that the oldest sorts last by name, the newest first, and sorting by id would delete a newer one. A target
    /// that cannot delete keeps all four and says so once. A manual backup and another installation's stay either way, even above Keep.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retention_RemovesTheOldestByAge_OrKeepsAllWhenTheTargetCannotDelete(bool canDelete)
    {
        _target.CanDelete = canDelete;
        _target.Entries["z-oldest"] = Start.AddDays(-3);
        _target.Entries["m-middle"] = Start.AddDays(-2);
        _target.Entries["a-newest"] = Start.AddDays(-1);

        // Older than all of them and above Keep, but not ours: a manual backup and another installation's.
        _target.Entries["manual-before-the-risky-change"] = Start.AddDays(-10);
        _target.Entries["other-installation"] = Start.AddDays(-9);
        await _store.SaveStateAsync(new BackupScheduleState(Written: ["z-oldest", "m-middle", "a-newest"]));
        await _store.SaveAsync(new BackupSchedule(Enabled: true, IntervalHours: 24, Keep: 3));

        await _scheduled.RunOnceAsync();

        if (canDelete)
        {
            Assert.Equal(5, _target.Entries.Count);
            Assert.DoesNotContain("z-oldest", _target.Entries.Keys);
            Assert.Contains("m-middle", _target.Entries.Keys);
            Assert.Contains("a-newest", _target.Entries.Keys);
            Assert.Empty(_toasts.Messages);
        }
        else
        {
            Assert.Equal(6, _target.Entries.Count);
            Assert.Single(_toasts.Messages);
        }

        Assert.Contains("manual-before-the-risky-change", _target.Entries.Keys);
        Assert.Contains("other-installation", _target.Entries.Keys);
    }

    private sealed class _Toasts : IToastService
    {
        public List<string> Messages { get; } = [];

        public void Show(string message, ToastSeverity severity, string? actionLabel = null, Action? onAction = null) => Messages.Add(message);
    }

    /// <summary>
    /// AC-1593: a local 72 hours and 3 kept under an organisation's floor of 24 and 7. Nine days of daily ticks write
    /// nine and keep seven; the local schedule alone would have written three and kept three. Nothing is written back.
    /// </summary>
    [Fact]
    public async Task AnOrganisationsFloor_BacksUpDaily_AndKeepsSeven()
    {
        var org = Substitute.For<IOrgPolicy>();
        org.ReadAsync(Arg.Any<CancellationToken>()).Returns(new ProvisionPolicy("Acme", Backup: new ProvisionBackupPolicy(MaxIntervalHours: 24, MinKeep: 7)));
        using var scheduled = new ScheduledBackup(_service, _store, _toasts, null, _clock, org) { TargetFor = _ => _target };
        await _store.SaveAsync(new BackupSchedule(Enabled: true, IntervalHours: 72, Keep: 3));
        await _store.SaveStateAsync(new BackupScheduleState(Start));

        foreach (var _ in Enumerable.Range(0, 9))
        {
            _clock.Advance(TimeSpan.FromHours(24));
            await scheduled.RunOnceAsync();
        }

        Assert.Equal(7, _target.Entries.Count);
        Assert.Equal(72, (await _store.LoadAsync()).IntervalHours);
    }

    /// <summary>
    /// AC-1593: an organisation that allows only Depot, on a machine with no Depot connection, gets no backup in the
    /// local folder: the run says why, on screen and on disk, and the clock stays put for the next tick.
    /// </summary>
    [Fact]
    public async Task NoTargetTheOrganisationAllows_IsSaid_AndNothingIsWritten()
    {
        var org = Substitute.For<IOrgPolicy>();
        org.ReadAsync(Arg.Any<CancellationToken>()).Returns(new ProvisionPolicy("Acme", Backup: new ProvisionBackupPolicy(AllowedTargets: ["depot"])));
        using var scheduled = new ScheduledBackup(_service, _store, _toasts, null, _clock, org) { TargetFor = _ => _target };
        await _store.SaveAsync(new BackupSchedule(Enabled: true, IntervalHours: 24));

        await scheduled.RunOnceAsync();

        Assert.Empty(_target.Entries);
        Assert.Contains("Acme allows it only to depot", Assert.Single(_toasts.Messages));
        Assert.Equal(Assert.Single(_toasts.Messages), (await _store.LoadStateAsync()).LastFailure);
    }

    private sealed class _Target : IBackupTarget
    {
        public Dictionary<string, DateTimeOffset> Entries { get; } = [];

        public Exception? Failure { get; set; }

        public bool CanDelete { get; set; } = true;

        public string Id => "fake";

        public string DisplayName => "fake";

        public Task<string> WriteAsync(string name, Stream archive, CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure)
            {
                throw failure;
            }

            // The newest there is, whatever the name: the one just made is never the one retention may take.
            Entries[name] = Start.AddDays(1);

            return Task.FromResult(name);
        }

        public Task<IReadOnlyList<BackupTargetEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupTargetEntry>>([.. Entries.Select(entry => new BackupTargetEntry(entry.Key, entry.Value, 1, null))]);

        public Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(CanDelete && Entries.Remove(id));
    }

    private sealed class _Service : IBackupService
    {
        public Task<BackupManifest> WriteAsync(Func<Stream, CancellationToken, Task> deliver, BackupOptions options, CancellationToken cancellationToken = default) =>
            Deliver(deliver, cancellationToken);

        private static async Task<BackupManifest> Deliver(Func<Stream, CancellationToken, Task> deliver, CancellationToken cancellationToken)
        {
            await deliver(new MemoryStream([1]), cancellationToken);

            return new BackupManifest(BackupManifest.CurrentSchema, "test", Start, false, [], new Dictionary<string, string>(), new Dictionary<string, string>());
        }

        public Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RestoreReport> RestoreAsync(string archivePath, RestoreOptions options, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RestoreReport> RestoreAsync(Stream source, RestoreOptions options, IProgress<RestoreProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // A clock the test moves and a timer it fires by hand: the real one would make this wait on the wall.
    private sealed class _ManualClock(DateTimeOffset now) : TimeProvider
    {
        private _Timer? _timer;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;

        public void FireTimer() => _timer?.Fire();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new _Timer(() => callback(state));

        private sealed class _Timer(Action fire) : ITimer
        {
            public void Fire() => fire();

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
