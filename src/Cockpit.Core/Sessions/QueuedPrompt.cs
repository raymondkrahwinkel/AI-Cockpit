namespace Cockpit.Core.Sessions;

// A prompt waiting for the turn in flight to end (T8), as the session host queues it. A class, not a record: two
// identical prompts are still two chips, and the queue removes the one that was clicked. AC-1438: a DTO the pane shows;
// `ReplyToRowId` (AC-935) names the row it answers, whose citation the host's consumer adds when it leaves.
public sealed class QueuedPrompt(string text, IReadOnlyList<ImageAttachment> images, string? replyToRowId = null)
{
    public string Text { get; } = text;

    public IReadOnlyList<ImageAttachment> Images { get; } = images;

    public string? ReplyToRowId { get; } = replyToRowId;
}
