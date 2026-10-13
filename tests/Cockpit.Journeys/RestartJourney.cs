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
    [Fact]
    public async Task AfterARestart_TheOpenSessionAndTheActiveDeskAreBack()
    {
        var stateRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-restart-{Guid.NewGuid():N}")).FullName;
        try
        {
            var deskId = "";
            string paneId;
            await using (var before = JourneyHost.Desktop(stateRoot: stateRoot))
            {
                var project = await before.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(stateRoot, "project")).FullName);
                await before.StartDesktopAsync();
                await HeadlessAvalonia.RunAsync(async () => deskId = (await before.Cockpit.Workspaces.CreateSessionsWorkspaceAsync("Journey")).Id);
                paneId = (await before.StartSessionThroughTheDialogAsync(project)).PaneId;

                // The desks are written in order through one store, so one more write of them lands after the pane's.
                await before.Services.GetRequiredService<IWorkspaceSettingsStore>().SaveAsync(HeadlessAvalonia.Run(() => before.Cockpit.Workspaces.Settings));
            }

            await using var after = JourneyHost.Desktop(stateRoot: stateRoot);
            await after.StartDesktopAsync();
            var (restored, activeDesk) = HeadlessAvalonia.Run(() => (
                after.Cockpit.Sessions.Select(session => (session.PaneId, session.WorkspaceId, Offered: session.RestoreOffer is not null)).ToList(),
                after.Cockpit.Workspaces.Active?.Id));

            Assert.Equal([(paneId, deskId, true)], restored);
            Assert.Equal(deskId, activeDesk);
        }
        finally
        {
            JourneyHost.RemoveStateRoot(stateRoot);
        }
    }

    // AC-1533 (follow-up of AC-1530): the green block is not in the log, so the repainted conversation builds it again —
    // after a restart the turn that made files still ends in the block with those files.
    [Fact]
    public async Task AfterARestart_ATurnThatMadeFilesStillEndsInItsGreenBlock()
    {
        var stateRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-restart-{Guid.NewGuid():N}")).FullName;
        try
        {
            string[] before = [];
            await using (var first = JourneyHost.Desktop(stateRoot: stateRoot))
            {
                var project = await first.SaveProfileAndProjectAsync(Directory.CreateDirectory(Path.Combine(stateRoot, "project")).FullName);
                await first.StartDesktopAsync();
                var session = await first.StartSessionThroughTheDialogAsync(project);
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
            }

            await using var after = JourneyHost.Desktop(stateRoot: stateRoot);
            await after.StartDesktopAsync();
            var restored = HeadlessAvalonia.Run(() => after.Cockpit.Sessions.OfType<SessionViewModel>().Single());
            Assert.True(HeadlessAvalonia.Run(() => restored.CanResumeConversation));
            HeadlessAvalonia.Run(() => restored.ResumeConversationCommand.Execute(null));
            await HeadlessAvalonia.RunAsync(() => Until.Holds(restored, () => _Blocks(restored).Length > 0 && restored.IsSessionReady));

            Assert.Equal(["Done · 3 files created"], before);
            Assert.Equal(before, HeadlessAvalonia.Run(() => _Blocks(restored)));
        }
        finally
        {
            JourneyHost.RemoveStateRoot(stateRoot);
        }
    }

    private static string[] _Blocks(SessionViewModel session) =>
        [.. session.Transcript.Where(row => row.TurnResult is not null).Select(row => row.TurnResult!.Title)];
}
