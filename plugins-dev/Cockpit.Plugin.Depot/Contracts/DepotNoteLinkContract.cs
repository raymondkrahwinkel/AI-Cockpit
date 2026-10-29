namespace Cockpit.Plugin.Depot.Contracts;

// AC-1581: a /n/{token} link resolved by the backend part, which alone holds the host-only token (DEP-534).
internal static class DepotNoteLinkChannel
{
    // Payload: DepotNoteLinkRequest. Answers a DepotNoteLinkAnswer; the token is never logged.
    public const string Resolve = "note-link-resolve";
}

internal static class DepotNoteLinkState
{
    public const string Ok = "ok";

    // Depot's one answer for no right, another organization, a bad token and a deleted note: nothing of the note comes with it.
    public const string Unavailable = "unavailable";

    public const string SignIn = "sign-in";

    public const string Unreachable = "unreachable";
}

internal static class DepotNoteRole
{
    public const string Editor = "editor";
    public const string Viewer = "viewer";
}

internal sealed record DepotNoteLinkRequest(string ConnectionId, string Token);

// Only an Ok answer carries a place; every other state is the bare state.
internal sealed record DepotNoteLinkAnswer(
    string State, string? Organization = null, string? Project = null, string? Path = null, string? Title = null, string? Role = null);

internal static class DepotNoteToken
{
    // Depot's tokens are opaque URL-safe text; anything else is not a note link and never reaches a URL.
    public static bool IsValid(string? token) =>
        token is { Length: > 0 and <= 128 } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
