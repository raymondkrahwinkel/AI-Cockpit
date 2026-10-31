namespace Cockpit.Plugin.Depot.Contracts;

internal static class DepotNotesChannel
{
    public const string Live = "notes-live";
    public const string Changed = "notes-changed";
}

internal sealed record DepotNotesRequest(string ConnectionId);

// Href is Depot's opaque /n/ link; People and the right to join come from Depot, never from the inbox.
internal sealed record DepotLiveNote(
    string Address, string Project, string Path, string Title, string Href, bool Join, IReadOnlyList<DepotNotePerson> People);

internal sealed record DepotNotePerson(string Name, string? Color = null);

// Reason: what Depot said when the state is not ok and not about signing in; null means plain unreachable.
internal sealed record DepotNotesPage(string ConnectionId, string State, IReadOnlyList<DepotLiveNote> Notes, string? Reason = null);
