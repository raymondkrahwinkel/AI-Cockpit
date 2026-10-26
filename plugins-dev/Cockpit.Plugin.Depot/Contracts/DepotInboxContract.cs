namespace Cockpit.Plugin.Depot.Contracts;

// AC-1586: the Depot inbox between the UI part and the backend part, which alone talks to Depot (REST and the inbox
// hub, with a host-only token the plugin never sees). One inbox per connection, never merged.
internal static class DepotInboxChannel
{
    // Payload: DepotInboxRequest. Answers the connection's DepotInboxPage, fetched again from Depot.
    public const string List = "inbox-list";

    // Payload ignored. Answers a DepotInboxCount per configured connection, for the side-menu badge.
    public const string Unread = "inbox-unread";

    // Payload: DepotInboxRequest with MessageId. Marks it read in Depot and answers the page after it.
    public const string MarkRead = "inbox-read";

    // Event, payload DepotInboxPage: one connection's inbox as it stands after a push from Depot or a fetch. In-process
    // only; Depot's own hub carries counts and never content.
    public const string Changed = "inbox-changed";
}

internal static class DepotInboxState
{
    public const string Ok = "ok";

    // Never signed in on this run: no counts, no titles, and nothing opens by itself (AC-1517).
    public const string SignIn = "sign-in";

    // Signed in earlier, refused now: the list is gone, since whoever signs in next may be someone else.
    public const string Expired = "expired";

    // Depot did not answer, or its hub did not connect: the last list stays, dimmed.
    public const string Unreachable = "unreachable";
}

internal sealed record DepotInboxRequest(string ConnectionId, string? MessageId = null);

internal sealed record DepotInboxCount(string ConnectionId, string State, int Unread, IReadOnlyDictionary<string, int> PerOrganization);

internal sealed record DepotInboxOrganization(string Slug, string Name);

// A NoteInvitation's note as Depot reads it for this account now. Title and Path are only there when Depot gave them.
internal sealed record DepotInboxNote(string State, string? Title, string? Path, bool Live, string? OpenUrl);

// OpenUrl (and Note.OpenUrl) are absolute: the backend puts the connection's address in front of Depot's relative link.
internal sealed record DepotInboxMessage(
    string Id, string Kind, bool IsRead, bool Handled, DateTimeOffset CreatedAt, DepotInboxOrganization? Organization,
    string Summary, string OpenUrl, DepotInboxNote? Note = null);

// UpdatedAt is when Depot last answered; on Unreachable the messages are that answer's.
internal sealed record DepotInboxPage(DepotInboxCount Count, IReadOnlyList<DepotInboxMessage> Messages, DateTimeOffset? UpdatedAt);

internal static class DepotInboxKind
{
    public const string NoteInvitation = "NoteInvitation";
}
