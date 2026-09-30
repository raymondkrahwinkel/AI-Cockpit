using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
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
}
