using System.Diagnostics;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// Layer 2 boundary: inbox pushes become notifications, with one invitation per connection, recipient and note.
internal sealed class DepotInvitationNotifications(
    ICockpitUiHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotInboxMessage, Task> join)
{
    private readonly HashSet<(string Connection, string Recipient, string NoteLink)> _shown = [];
    private readonly HashSet<(string Connection, string Recipient)> _seeded = [];

    public void Show(DepotInboxPage page)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == page.Count.ConnectionId);
        if (connection is null || page.Count.State != DepotInboxState.Ok || string.IsNullOrWhiteSpace(page.Recipient))
        {
            return;
        }

        var seed = _seeded.Add((connection.Id, page.Recipient));
        foreach (var message in page.Messages)
        {
            if (message.Kind != DepotInboxKind.NoteInvitation || message.IsRead || message.Handled
                || message.Note?.OpenUrl is not { Length: > 0 } noteLink
                || !_shown.Add((connection.Id, page.Recipient, noteLink)))
            {
                continue;
            }

            if (seed)
            {
                continue;
            }

            var (title, body) = Text(connection.Name, message);
            Action onJoin = () => { _ = _JoinAsync(message); };
            host.ShowToast($"{title}\n{body}", actionLabel: "Join", onAction: onJoin);
            host.ShowSystemNotification(title, body, onJoin);
        }
    }

    public static (string Title, string Body) Text(string connectionName, DepotInboxMessage message)
    {
        var invitation = message.Summary.IndexOf(" invites you", StringComparison.Ordinal);
        var inviter = invitation >= 0 ? message.Summary[..invitation] : message.Summary;
        var hasTitle = !string.IsNullOrWhiteSpace(message.Note?.Title);
        var title = $"{inviter} invited you to {(hasTitle ? message.Note?.Title : "a note")}";
        List<string> details = [connectionName];
        if (hasTitle && message.Note is { } note)
        {
            if (!string.IsNullOrWhiteSpace(note.Project))
            {
                details.Add(note.Project);
            }

            if (note.Participants is { Count: > 0 })
            {
                details.Add(string.Join(", ", note.Participants));
            }
        }

        return (title, string.Join(" · ", details));
    }

    private async Task _JoinAsync(DepotInboxMessage message)
    {
        try
        {
            await join(message).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Joining a Depot note from its invitation failed: {exception}");
        }
    }
}
