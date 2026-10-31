namespace Cockpit.Plugin.Depot.Contracts;

// AC-1897: a live note between the UI part and the backend part, which alone talks to /hubs/notes with a host-only
// token the plugin never sees. Nothing here carries a credential: a status is a state and a fixed sentence.
internal static class DepotNoteLiveChannel
{
    // Payload: DepotNoteLiveRequest. Opens the note's connection (or signs it in again after SignInNeeded); answers true.
    public const string Open = "note-live-open";

    // Payload: DepotNoteLiveRequest. Closes it; answers true.
    public const string Close = "note-live-close";

    // Event, payload DepotNoteLiveStatus: the connection's state changed. In-process only.
    public const string Status = "note-live-status";
}

internal static class DepotNoteLiveState
{
    public const string Connecting = "connecting";

    public const string Live = "live";

    // The connection dropped (Depot closes it after five minutes at most); it joins again with the last epoch.
    public const string Reconnecting = "reconnecting";

    // The room was refilled while this client was away: its own text does not go in. The user decides (AC-1903).
    public const string Stale = "stale";

    // Not signed in, or the session ended. One attempt, then nothing until the user signs in again.
    public const string SignInNeeded = "sign-in-needed";

    // Signed in again, but as someone else than the note was opened as (AC-1582): no Join, no save, until that account is back.
    public const string OtherAccount = "other-account";

    public const string Unavailable = "unavailable";

    public const string Disabled = "disabled";
}

internal sealed record DepotNoteLiveRequest(string ConnectionId, string Organization, string Project, string Path);

internal sealed record DepotNoteLiveStatus(string ConnectionId, string Organization, string Project, string Path, string State, string? Message = null);
