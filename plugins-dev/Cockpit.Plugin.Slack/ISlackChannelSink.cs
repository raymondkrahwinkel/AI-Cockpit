namespace Cockpit.Plugin.Slack;

/// <summary>
/// The Slack-agnostic seam a real gateway connection posts/edits/reacts through (AC-1025). Kept separate from
/// <see cref="Cockpit.Plugins.Abstractions.Channels.IAssistantChannelGateway"/> so <see cref="SlackChannelBridge"/>'s
/// routing logic is testable without a live socket. Message identity is Slack's own <c>ts</c> string rather than
/// Discord's numeric snowflake — Slack has no equivalent numeric id.
/// </summary>
internal interface ISlackChannelSink
{
    /// <summary>
    /// Posts a new message, optionally with Approve/Deny buttons for <paramref name="consentPromptId"/>.
    /// Returns the new message's <c>ts</c>.
    /// </summary>
    Task<string> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits a message already returned by <see cref="PostAsync"/>. <paramref name="keepButtons"/> false strips
    /// any consent buttons.
    /// </summary>
    Task EditAsync(string messageTs, string text, bool keepButtons = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reaction emoji (by bare name, e.g. <c>"warning"</c>) to an inbound message — the plugin's only
    /// reply to a sender-visible failure.
    /// </summary>
    Task AddReactionAsync(string messageTs, string emoji, CancellationToken cancellationToken = default);
}
