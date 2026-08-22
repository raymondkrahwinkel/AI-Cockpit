namespace Cockpit.Plugin.Discord;

/// <summary>
/// The Discord-agnostic seam a real gateway connection posts/edits/reacts through (AC-1024). Kept separate from
/// <see cref="Cockpit.Plugins.Abstractions.Channels.IAssistantChannelGateway"/> so <see cref="DiscordChannelBridge"/>'s
/// routing logic is testable without a live socket.
/// </summary>
internal interface IDiscordChannelSink
{
    /// <summary>
    /// Posts a new message, optionally with Approve/Deny buttons for <paramref name="consentPromptId"/>.
    /// Returns the new message's id.
    /// </summary>
    Task<ulong> PostAsync(string text, Guid? consentPromptId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits a message already returned by <see cref="PostAsync"/>. <paramref name="keepButtons"/> false strips
    /// any consent buttons.
    /// </summary>
    Task EditAsync(ulong messageId, string text, bool keepButtons = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a reaction emoji to an inbound message — the plugin's only reply to a sender-visible failure.
    /// </summary>
    Task AddReactionAsync(ulong messageId, string emoji, CancellationToken cancellationToken = default);
}
