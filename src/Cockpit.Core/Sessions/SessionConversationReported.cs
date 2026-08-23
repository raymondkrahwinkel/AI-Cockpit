namespace Cockpit.Core.Sessions;

// The payload of `SessionConversationTracker.Changed`: `PaneId` is the pane whose conversation id
// changed, `Conversation` is its new one.
public sealed record SessionConversationReported(string PaneId, SessionConversationId Conversation);
