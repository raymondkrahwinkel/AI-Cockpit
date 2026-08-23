namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// What one transcript row of a TTY session <em>is</em>, in the coarse vocabulary the host renders and the
/// assistant reads (AC-609) — the reading counterpart of <see cref="PluginSessionActivity"/>. Deliberately the
/// same short list for every provider: the host maps these onto its own transcript kinds, so a provider says what
/// a row means without either side learning the other's names.
/// </summary>
public enum PluginTranscriptEntryKind
{
    /// <summary>
    /// What the operator (or a sent prompt) said.
    /// </summary>
    UserText,

    /// <summary>
    /// The model's prose.
    /// </summary>
    AssistantText,

    /// <summary>
    /// The model calling a tool — the name, and whatever the provider can say about the arguments.
    /// </summary>
    ToolUse,

    /// <summary>
    /// What a tool call returned, carried on the call it belongs to where the provider can pair them.
    /// </summary>
    ToolResult,

    /// <summary>
    /// A reasoning/extended-thinking block.
    /// </summary>
    Thinking,

    /// <summary>
    /// An error the transcript recorded.
    /// </summary>
    Error,
}

/// <summary>
/// One already-written row of a TTY session's transcript, read back on demand (AC-609) rather than tailed live.
/// A TTY session hosts the provider's real TUI, so the on-disk transcript is the only record of what it did —
/// this is how the cockpit's read surfaces (the assistant's <c>read_transcript</c>) get at it without the host
/// learning any provider's format.
/// </summary>
/// <param name="Kind">
/// What this row is.
/// </param>
/// <param name="Text">
/// The row's text: the message, the thinking, or a tool call's name and arguments.
/// </param>
/// <param name="ToolResult">
/// On a <see cref="PluginTranscriptEntryKind.ToolUse"/> row, what that call returned, when the provider can pair the two. Null everywhere else.
/// </param>
public sealed record PluginTranscriptEntry(
    PluginTranscriptEntryKind Kind,
    string Text,
    string? ToolResult = null);

/// <summary>
/// The tail of a transcript and how long the whole thing is (AC-609). One type rather than two calls because the
/// total is a by-product of the read that produced the slice, and a second pass over a multi-megabyte file to
/// count what was just counted is the kind of thing nobody notices until a session has been running all day.
/// </summary>
/// <param name="Entries">
/// The last rows asked for, oldest first.
/// </param>
/// <param name="TotalEntries">
/// How many rows the transcript holds in all — never less than <see cref="Entries"/>'s count.
/// </param>
public sealed record PluginTranscriptSlice(IReadOnlyList<PluginTranscriptEntry> Entries, int TotalEntries)
{
    /// <summary>
    /// Nothing read: what a provider reports for a session with no transcript it can name, and what the interface default answers.
    /// </summary>
    public static PluginTranscriptSlice Empty { get; } = new([], 0);
}
