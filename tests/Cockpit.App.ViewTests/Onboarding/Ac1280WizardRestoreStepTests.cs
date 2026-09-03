using NSubstitute;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// AC-1280: putting a backup back from the first-run wizard, and — the whole ticket — what the wizard does next.
/// </summary>
/// <remarks>
/// Driven through the wizard rather than through <see cref="IBackupService"/>: the restore itself was already
/// finished and tested when this ticket started, and the defect it exists for is entirely in what the wizard does
/// after one — carrying on over settings that were just restored, or starting a cockpit on top of them.
/// </remarks>
[Collection("avalonia")]
public sealed class Ac1280WizardRestoreStepTests
{
    [Theory]
    // A restore that ran to the end takes the wizard over: it is already marked complete and the process is
    // restarting, so the startup route's own close handling must not run a second time on the way out.
    [InlineData("restored", true, "Restored.")]
    // A restore that threw changed nothing, so the wizard is simply still running — and says why it failed.
    [InlineData("failed", false, "This backup uses the old layout")]
    // The fourth state the ticket does not name: the operator stopped it. Stopping is only offered while the
    // settings have not been written, so this machine is untouched — neither a success to restart into nor a
    // failure to report. The wizard carries on exactly as it would have.
    [InlineData("stopped", false, "your settings are unchanged")]
    // Skipped: the step was never used, and nothing about the wizard is different from before this ticket.
    [InlineData("skipped", false, "")]
    public Task WhatTheWizardDoesNext_FollowsTheRestore_RatherThanItsOwnUsualRoute(
        string outcome,
        bool takesTheWizardOver,
        string status) =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var stateStore = Substitute.For<IFirstRunWizardStateStore>();
            var restart = Substitute.For<IAppRestartService>();
            var backups = new _RestoreThat(outcome);
            var step = new RestoreStep(new RestoreStepViewModel(backups, stateStore, restart));
            var wizard = new FirstRunWizardViewModel([new WelcomeStep(), step], FirstRunWizardViewModel.EpicPlan);

            wizard.NextCommand.Execute(null);

            if (outcome == "stopped")
            {
                backups.WhenReported = step.ViewModel.Stop;
            }

            if (outcome != "skipped")
            {
                await step.ViewModel.RestoreAsync("backup.zip");
            }

            Assert.Equal(takesTheWizardOver, step.ViewModel.TookOver);
            Assert.Contains(status, step.ViewModel.Status);
            restart.Received(takesTheWizardOver ? 1 : 0).Restart();

            // The wizard never walks on by itself, whatever happened: the operator is still looking at this step.
            Assert.True(wizard.StepBar[1].IsCurrent);
            await stateStore.Received(takesTheWizardOver ? 1 : 0)
                .MarkCompletedAsync(FirstRunWizardVersion.Current, Arg.Any<CancellationToken>());

            // The window closing is what the startup route hangs its own finish on. After a restore that must do
            // nothing at all — no second cockpit over the restored settings, and no second completion marker.
            var cockpitsStarted = 0;
            wizard.FinishFromStartup(stateStore, () => cockpitsStarted++);

            Assert.Equal(takesTheWizardOver ? 0 : 1, cockpitsStarted);
            await stateStore.Received(1).MarkCompletedAsync(FirstRunWizardVersion.Current, Arg.Any<CancellationToken>());
        });

    // Fails, stops or restores on command, and reports one stage on the way so a stop has a moment to land — the
    // real service honours a token up to the write stage and reports the outcome instead of throwing it.
    private sealed class _RestoreThat(string outcome) : IBackupService
    {
        public Action? WhenReported { get; set; }

        public Task<BackupManifest> WriteAsync(string archivePath, BackupOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("These tests only restore.");

        public Task<BackupManifest> ReadManifestAsync(string archivePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BackupManifest(
                BackupManifest.CurrentSchema,
                "test",
                DateTimeOffset.UnixEpoch,
                IncludesCredentials: false,
                [],
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["boards"] = "1.0.0" }));

        public Task<RestoreReport> RestoreAsync(
            string archivePath,
            RestoreOptions options,
            IProgress<RestoreProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (outcome == "failed")
            {
                throw new InvalidOperationException("This backup uses the old layout 1; this cockpit reads 2.");
            }

            progress?.Report(new RestoreProgress(RestoreStage.FetchingPlugins, 1, 1));
            WhenReported?.Invoke();

            return Task.FromResult(new RestoreReport(cancellationToken.IsCancellationRequested, []));
        }
    }
}
