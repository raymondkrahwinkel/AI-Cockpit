using System.Diagnostics;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-1579: the way in to the "New meeting note" dialog. AC-1577's button calls ShowAsync; until it does, the
// global toolbar reaches it. The first connection is used, since an operator mostly has one Depot.
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
            NewMeetingNoteDialog? dialog = null;
            await host.ShowDialogAsync("New meeting note", () => dialog = new NewMeetingNoteDialog(
                notes, (project, path) => OpenAsync(notes, dialog, project, path), DateOnly.FromDateTime(DateTime.Now)), "depot-new-meeting-note", 560, 700).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Opening the new meeting note dialog failed: {exception}");
        }
    }

    // Until the editor tab (AC-1580) joins the live note, "Open in Cockpit" opens Depot's own link to it in the browser.
    private static async Task OpenAsync(IDepotMeetingNotes notes, Control? owner, string project, string path)
    {
        var link = await notes.LinkAsync(project, path).ConfigureAwait(true);
        if (!Uri.TryCreate(link.Data, UriKind.Absolute, out var uri) || owner is null || TopLevel.GetTopLevel(owner) is not { } top
            || !await top.Launcher.LaunchUriAsync(uri).ConfigureAwait(true))
        {
            throw new InvalidOperationException(link.Message ?? "The note could not be opened.");
        }
    }
}
