namespace Cockpit.Core.Sessions;

// The payload of `SessionConversationTracker.Changed` — which pane, and its new conversation id.
//
// `PaneId`: The pane whose conversation id changed.
// `Conversation`: Its new conversation id.
public sealed record SessionConversationReported(string PaneId, SessionConversationId Conversation);
