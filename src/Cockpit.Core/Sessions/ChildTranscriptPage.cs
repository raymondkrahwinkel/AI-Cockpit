namespace Cockpit.Core.Sessions;

public sealed record ChildTranscriptEntry(string Id, string Kind, string Text);

public sealed record ChildTranscriptPage(
    IReadOnlyList<ChildTranscriptEntry> Entries, string? Cursor, string Status, bool Reset = false)
{
    public static ChildTranscriptPage Unavailable(string reason) => new([], null, $"Not available: {reason}");
}
