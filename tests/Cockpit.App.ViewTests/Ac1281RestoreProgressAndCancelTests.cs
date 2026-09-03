using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Backup;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1281: a restore that says where it is, that can be stopped for as long as stopping still costs nothing, and
/// that never answers a click with silence.
/// </summary>
/// <remarks>
/// Every test here goes through <see cref="CockpitViewModel.RestoreBackupAsync"/> rather than the service, because
/// the bug this ticket exists for was not in the service: the service already took an <c>IProgress</c> and a token,
/// and the view model passed neither. A test that called the service directly would have been green throughout.
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1281RestoreProgressAndCancelTests
{
    [Theory]
    // Unpacking happens entirely inside staging, so the offer to stop is live, the token gets through, and the
    // restore ends without having touched the cockpit.
    [InlineData(RestoreStage.Unpacking, 0, 0, true, "Unpacking the archive…", "stopped")]
    // The fetch runs before cockpit.json is rewritten and is where the minutes go, so it stays stoppable too — the
    // boundary AC-1278 put before the write stage moved here on Raymond's decision. Also the one stage with a
    // number worth showing: plugins are countable, an archive's files are not.
    [InlineData(RestoreStage.FetchingPlugins, 3, 11, true, "Fetching plugins… 3 of 11.", "stopped")]
    // Past that line a half-written cockpit directory is the risk, so the offer is withdrawn — the button is on
    // screen and dead, and the restore runs to the end.
    [InlineData(RestoreStage.Writing, 0, 0, false, "Putting the settings back…", "Restored")]
    public Task EachStageIsReported_AndStoppingIsOnlyOfferedWhileItStillCostsNothing(
        RestoreStage stage,
        int done,
        int total,
        bool stoppingStillOffered,
        string statusWhileRunning,
        string statusAtTheEnd) =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var backups = new _ReportsOneStage(new RestoreProgress(stage, done, total));
            var cockpit = _Cockpit(backups);

            backups.WhenReported = () =>
            {
                backups.SeenStatus = cockpit.BackupStatus;
                backups.SeenCanCancel = cockpit.CanStopBackup;
                backups.SeenIsRunning = cockpit.IsBackupRunning;

                // Asking to stop from the one moment the operator could: whether it lands is what is being tested.
                cockpit.StopBackup();
            };

            await cockpit.RestoreBackupAsync("unused.zip", _ => Task.FromResult<RestoreOptions?>(new RestoreOptions(true, [])));

            Assert.Equal(statusWhileRunning, backups.SeenStatus);
            Assert.Equal(stoppingStillOffered, backups.SeenCanCancel);
            Assert.True(backups.SeenIsRunning);
            Assert.Contains(statusAtTheEnd, cockpit.BackupStatus);

            // Whatever the outcome, the run is over: neither flag may be left standing, or the button never returns.
            Assert.False(cockpit.IsBackupRunning);
            Assert.False(cockpit.CanStopBackup);
        });

    [Fact]
    public Task WithoutABackupService_BothCommandsSaySoInsteadOfReturningInSilence() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var cockpit = new CockpitViewModel();

            await cockpit.CreateBackupAsync("unused.zip");
            var afterBackup = cockpit.BackupStatus;

            cockpit.BackupStatus = string.Empty;
            await cockpit.RestoreBackupAsync("unused.zip", _ => Task.FromResult<RestoreOptions?>(new RestoreOptions(true, [])));

            Assert.NotEmpty(afterBackup);
            Assert.NotEmpty(cockpit.BackupStatus);
            Assert.Equal(afterBackup, cockpit.BackupStatus);
        });

    [Theory]
    // A fetch that ran to the end: whatever refused is named with the reason it gave.
    [InlineData(false)]
    // A fetch the operator stopped: nothing is rolled back, so the same naming is owed — a stopped restore that
    // says only "stopped" leaves the operator guessing which half of their plugins is now there.
    [InlineData(true)]
    public Task PluginsStillMissingAfterTheFetch_AreNamedWithTheirReason(bool stopped) =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var backups = new _ReportsOneStage(new RestoreProgress(RestoreStage.FetchingPlugins, 1, 2))
            {
                Missing =
                [
                    new RestoreMissingPlugin("boards", "its store could not be reached"),
                    new RestoreMissingPlugin("voice", "this cockpit is too old for the version in the archive"),
                ],
            };

            var cockpit = _Cockpit(backups);

            if (stopped)
            {
                backups.WhenReported = cockpit.StopBackup;
            }

            await cockpit.RestoreBackupAsync("unused.zip", _ => Task.FromResult<RestoreOptions?>(new RestoreOptions(true, ["boards", "voice"])));

            Assert.Contains("boards", cockpit.BackupStatus);
            Assert.Contains("its store could not be reached", cockpit.BackupStatus);
            Assert.Contains("voice", cockpit.BackupStatus);
            Assert.Contains("this cockpit is too old for the version in the archive", cockpit.BackupStatus);

            // A stop leaves the settings alone, and the line has to say so rather than read like a finished restore.
            Assert.Equal(stopped, cockpit.BackupStatus.Contains("unchanged", StringComparison.Ordinal));
        });

    private static CockpitViewModel _Cockpit(IBackupService backups)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            backupService: backups);
    }

    // Reports one stage, lets the test look at the view model at that exact moment, and then honours the token the
    // way the real service does: only up to the write stage, since past it the token is deliberately not passed on.
    private sealed class _ReportsOneStage(RestoreProgress stage) : IBackupService
    {
        public Action? WhenReported { get; set; }

        public IReadOnlyList<RestoreMissingPlugin> Missing { get; init; } = [];

        public string? SeenStatus { get; set; }

        public bool SeenCanCancel { get; set; }

        public bool SeenIsRunning { get; set; }

        public Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests only restore.");

        public Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupManifest(
                BackupManifest.CurrentSchema, "test", DateTimeOffset.UnixEpoch, false, [], new Dictionary<string, string>(), new Dictionary<string, string>()));

        public Task<RestoreReport> RestoreAsync(
            string archivePath,
            RestoreOptions options,
            IProgress<RestoreProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(stage);
            WhenReported?.Invoke();

            // One exit, like the real service: a stop is reported back, never thrown. And ignored once the write
            // stage has begun, also like the real service — past that line the token is not passed on at all.
            return Task.FromResult(new RestoreReport(
                Stopped: stage.Stage != RestoreStage.Writing && cancellationToken.IsCancellationRequested,
                Missing));
        }
    }
}
