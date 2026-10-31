using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Schedules.UI;

// AC-1547: where the screen's tasks live, this computer or a server. Everything comes from that place's host: its
// channel, profiles and project folders, so a task edited on the server never touches this computer (one home per task).
internal sealed record SchedulesSource(string? Server, ICockpitUiHost Host, SchedulesState State, IReadOnlyList<string> Profiles, IReadOnlyList<string> Folders)
{
    public static async Task<SchedulesSource> OpenAsync(ICockpitUiHost host, string? server = null)
    {
        var state = new SchedulesState(host.Channel);
        try
        {
            await state.RefreshAsync();
            var profiles = (await host.GetProfilesAsync()).Select(profile => profile.Label).ToList();
            var folders = (await host.GetProjectsAsync()).Select(project => project.SourceDirectory).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            return new SchedulesSource(server, host, state, profiles, folders);
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }
}
