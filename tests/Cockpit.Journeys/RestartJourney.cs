using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Workspaces;

namespace Cockpit.Journeys;

// J7, the nightly update's restart: the cockpit closed and built again on the same state root brings back the
// session that was open and the desk that was active. Two hosts one after the other; no timer between them.
[Collection(JourneyCollection.Alone)]
public sealed class RestartJourney
{
    // AC-1533: the green block is not in the log, so after a restart the repainted turn that made files builds it again.
    // AC-1502: the scheduled backup set in Options -> Backup is still set after a restart, where every start's planner
    // (and a server's) reads it from.
    [Fact]
    public async Task AfterARestart_TheOpenSessionAndTheActiveDeskAreBack()
    {
        var stateRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-restart-{Guid.NewGuid():N}")).FullName;
        try
        {
            var deskId = "";
            var paneId = "";
            string[] before = [];
            await Step.Run("before the restart", async () =>
            {
                await using (var first = JourneyHost.Desktop(stateRoot: stateRoot))
                {
                    var project = await first.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(stateRoot, "project")).FullName);
                    await first.StartDesktopAsync();
                    await HeadlessAvalonia.RunAsync(async () => deskId = (await first.Cockpit.Workspaces.CreateSessionsWorkspaceAsync("Journey")).Id);
                    var session = await first.StartSessionThroughTheDialogAsync(project);
                    paneId = session.PaneId;
                    await HeadlessAvalonia.RunAsync(async () =>
                    {
                        session.InputText = "hello";
                        await session.SendCommand.ExecuteAsync(null);
                        await Until.Holds(session, () => session.SessionStatus == SessionStatus.Done);
                        session.InputText = EchoDriver.WorkingPrompt;
                        await session.SendCommand.ExecuteAsync(null);
                        first.Driver.FinishWriting();
                        await Until.Holds(session, () => _Blocks(session).Length > 0 && !session.IsBusy);
                        before = _Blocks(session);
                    });

                    var schedule = HeadlessAvalonia.Run(() => first.Cockpit.ScheduledBackupSettings) ?? throw new InvalidOperationException("The desktop has no scheduled backup settings.");
                    await HeadlessAvalonia.RunAsync(async () =>
                    {
                        await schedule.LoadAsync();
                        schedule.Enabled = true;
                        schedule.Interval = BackupScheduleViewModel.Weekly;
                        schedule.Keep = 3;
                        await schedule.Saving;
                    });

                    // The desks are written in order through one store, so one more write of them lands after the pane's.
                    await first.Services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(HeadlessAvalonia.Run(() => first.Cockpit.Workspaces.Settings));
                }
            });

            await using var after = JourneyHost.Desktop(stateRoot: stateRoot);
            await after.StartDesktopAsync();
            await Step.Run("the open session and the active desk are back", () =>
            {
                var (restored, activeDesk) = HeadlessAvalonia.Run(() => (
                    after.Cockpit.Sessions.Select(session => (session.PaneId, session.WorkspaceId, Offered: session.RestoreOffer is not null)).ToList(),
                    after.Cockpit.Workspaces.Active?.Id));

                Assert.Equal([(paneId, deskId, true)], restored);
                Assert.Equal(deskId, activeDesk);
                return Task.CompletedTask;
            });

            await Step.Run("the turn that made files still ends in its green block", async () =>
            {
                var reopened = HeadlessAvalonia.Run(() => after.Cockpit.Sessions.OfType<SessionViewModel>().Single());
                Assert.True(HeadlessAvalonia.Run(() => reopened.CanResumeConversation));
                HeadlessAvalonia.Run(() => reopened.ResumeConversationCommand.Execute(null));
                await HeadlessAvalonia.RunAsync(() => Until.Holds(reopened, () => _Blocks(reopened).Length > 0 && reopened.IsSessionReady));
                Assert.Equal(["Done · 3 files created"], before);
                Assert.Equal(before, HeadlessAvalonia.Run(() => _Blocks(reopened)));
            });

            await Step.Run("the scheduled backup is still switched on with its keep", async () =>
            {
                var restoredSchedule = HeadlessAvalonia.Run(() => after.Cockpit.ScheduledBackupSettings) ?? throw new InvalidOperationException("The desktop has no scheduled backup settings.");
                await HeadlessAvalonia.RunAsync(() => restoredSchedule.LoadAsync());
                Assert.Equal((true, BackupScheduleViewModel.Weekly, 3), HeadlessAvalonia.Run(() => (restoredSchedule.Enabled, restoredSchedule.Interval, restoredSchedule.Keep)));
            });
        }
        finally
        {
            JourneyHost.RemoveStateRoot(stateRoot);
        }
    }

    private static string[] _Blocks(SessionViewModel session) =>
        [.. session.Transcript.Select(row => row.TurnResult?.Title).OfType<string>()];
}
