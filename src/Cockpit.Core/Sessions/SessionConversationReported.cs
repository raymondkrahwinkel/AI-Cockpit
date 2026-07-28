namespace Cockpit.Core.Sessions;

/// <summary>The payload of <see cref="SessionConversationTracker.Changed"/> — which pane, and its new conversation id.</summary>
/// <param name="PaneId">The pane whose conversation id changed.</param>
/// <param name="Conversation">Its new conversation id.</param>
public sealed record SessionConversationReported(string PaneId, SessionConversationId Conversation);
