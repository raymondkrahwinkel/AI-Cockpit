using System.Diagnostics;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-1579: the way in to the "New meeting note" dialog; AC-1577's button in the Notes tab calls ShowAsync.
// The first connection is used, since an operator mostly has one Depot.
internal static class DepotMeetingNotes
{
    public static async Task ShowAsync(ICockpitUiHost host, IReadOnlyList<DepotConnectionRegistration> connections)
    {
        try
        {
            if (connections.FirstOrDefault() is not { } connection)
            {
                host.ShowToast("Connect a Depot first, in Depot settings.");
                return;
            }

            var notes = new DepotMeetingNoteModel(host, connection.Id);
            await host.ShowDialogAsync("New meeting note", () => new NewMeetingNoteDialog(
                notes, (project, path) => DepotMarkdownFiles.OpenNoteAsync(host, connection, project, path, edit: true), DateOnly.FromDateTime(DateTime.Now), defaultProject: () => DefaultProjectAsync(host)), "depot-new-meeting-note", 560, 700).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening the new meeting note dialog failed: {exception}");
        }
    }

    // The Depot project the active Cockpit project keeps its memory in (a `depot…:<project>` reference), if any.
    private static async Task<string?> DefaultProjectAsync(ICockpitUiHost host)
    {
        var rows = await host.GetProjectMemoryRowsAsync(host.ActivePaneId).ConfigureAwait(true);
        var reference = rows.Select(row => row.Reference).FirstOrDefault(value => value.StartsWith("depot", StringComparison.OrdinalIgnoreCase));
        return reference is null ? null : reference[(reference.IndexOf(':') + 1)..];
    }
}
