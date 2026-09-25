using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.TranscriptSearch;

// Search over the `claude` CLI's on-disk transcripts, as a plugin (AC-1395, pure UI per F2.1): its only
// contributions — a left-menu button, the `Ctrl+F` shortcut it used to own as a built-in action, and the
// New-session dialog's conversation picker — all need a window, so this project has no backend part at all.
public sealed class TranscriptSearchUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        // One dialog for standalone search: reopening while it's up should refocus it, not stack a second one.
        void OpenSearch() => _ = host.ShowDialogAsync(
            "Search transcripts",
            () => new TranscriptSearchDialogControl(new TranscriptSearchService(host), host),
            "search",
            width: 820,
            height: 600);

        host.AddSideMenuButton("Search transcripts", OpenSearch);
        host.AddShortcut(new PluginShortcut("transcript-search.open", "Search transcripts", "Ctrl+F", OpenSearch));

        // The New-session dialog can resume a conversation by id, and typing one by hand is a poor way to find
        // it. The cockpit knows nothing about claude's transcripts — this plugin does — so it offers the search
        // as the picker behind that dialog's Search button.
        async Task<PickedConversation?> SearchForConversationAsync()
        {
            PickedConversation? picked = null;
            // No key: this dialog hands its pick back to the awaiting New-session dialog, so a second call must
            // get its own instance and its own answer rather than the first caller's still-pending Task.
            await host.ShowDialogAsync(
                "Search transcripts",
                () => new TranscriptSearchDialogControl(
                    new TranscriptSearchService(host),
                    host,
                    hit => picked = new PickedConversation(hit.SessionId, hit.WorkingDirectory)),
                820,
                600);
            return picked;
        }

        host.AddConversationPicker(new ConversationPickerRegistration(
            "Search transcripts",
            async () => (await SearchForConversationAsync())?.SessionId)
        {
            // The transcript records each session's cwd, so hand the directory back with the id — the dialog
            // starts the resumed session there, where claude keeps that session's transcript.
            PickWithLocationAsync = SearchForConversationAsync,
        });
    }
}
