using System.Text.Json;
using Avalonia.Threading;
using Cockpit.Plugin.SessionReview.Contracts;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;

namespace Cockpit.Plugin.SessionReview.UI;

// The UI part of Session Review (AC-1395): the "Review changes…" header action and the panel it opens
// (SessionDiffDialogControl), both needing a window. The backend part, SessionReviewPlugin, keeps the intent
// handler (no ICockpitUiHost equivalent) and forwards an open request here over the plugin's channel.
public sealed class SessionReviewUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Review changes…",
            string.Empty,
            session => _ = _OpenAsync(host, session))
        {
            IconKind = MaterialIconKind.FileCompare,
        });

        // The backend's channel event runs on its own publishing thread, not the UI thread (see
        // IPluginUiChannel.Subscribe) — marshal before building the dialog's controls.
        host.Channel.Subscribe(SessionReviewChannel.OpenEvent, channelEvent =>
        {
            var request = channelEvent.Payload.Deserialize<SessionReviewOpenRequest>(SessionReviewChannel.Json);
            if (request is not null)
            {
                Dispatcher.UIThread.Post(() => _ = _OpenAsync(host, new IntentSession(request.PaneId, request.WorkingDirectory)));
            }
        });
    }

    // One review dialog per pane: reopening for the same session should refocus it, not stack another.
    private static Task _OpenAsync(ICockpitUiHost host, IPluginSessionContext session) => host.ShowDialogAsync(
        "Session review",
        () => new SessionDiffDialogControl(host, session),
        $"review.{session.PaneId}",
        // Wider and taller than the old flat list needed: the tree takes a fixed 260 on the left, and what
        // is left has to hold a line of code plus two number gutters without wrapping every other line.
        width: 1100,
        height: 720);

    // An intent (relayed as a channel event) carries strings, not a live session, so the caller's pane and
    // directory come across as a snapshot — enough for the panel, which reads both only when it loads a diff.
    private sealed class IntentSession(string paneId, string? workingDirectory) : IPluginSessionContext
    {
        public string PaneId { get; } = paneId;

        public string? WorkingDirectory { get; } = workingDirectory;

        public event EventHandler? WorkingDirectoryChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }
    }
}
