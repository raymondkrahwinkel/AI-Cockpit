using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.TranscriptSearch;

// Search over the `claude` CLI's on-disk transcripts, as a plugin: the JSONL history and its schema belong
// to that one provider, so in a cockpit that drives several they have no business in the core. Contributes a
// left-menu button and the `Ctrl+F` shortcut it used to own as a built-in action, both opening the search
// dialog. It needs no services of its own — the search reads the profiles from the host on every query.
public sealed class TranscriptSearchPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "transcript-search",
        DisplayName: "Claude Transcript Search",
        Author: "Cockpit",
        Description: "Search everything you and the agent ever wrote in a Claude CLI session, across every Claude profile you have configured.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // One dialog for standalone search: reopening while it's up should refocus it, not stack a second one.
        void OpenSearch() => _ = host.ShowDialogAsync(
            "Search transcripts",
            () => new TranscriptSearchDialogControl(new TranscriptSearchService(host), host.Actions),
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
                    host.Actions,
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

    public void Dispose()
    {
    }
}
