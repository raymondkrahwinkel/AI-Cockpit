namespace Cockpit.Plugins.Abstractions.Sessions;

public sealed record PluginChildTranscriptPage(
    IReadOnlyList<PluginChildTranscriptEntry> Entries, string? Cursor, string Status, bool Reset = false)
{
    public static PluginChildTranscriptPage Unavailable(string reason) => new([], null, $"Not available: {reason}");
}
