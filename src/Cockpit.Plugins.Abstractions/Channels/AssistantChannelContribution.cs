namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// How much of the assistant's transcript a channel is worth relaying (AC-1023 §1.4). One setting per plugin
/// instance, not per user. The translating into a platform message is the plugin's — this only says how much.
/// </summary>
public enum AssistantChannelVerbosity
{
    /// <summary>A — the assistant's finished answers, nothing else.</summary>
    FinalAnswerOnly,

    /// <summary>B — everything the chat window shows, tool use included.</summary>
    Everything,

    /// <summary>C — short status lines in place of the full tool traffic.</summary>
    StatusLines,
}

/// <summary>
/// What a plugin hands the host via <see cref="ICockpitHost.OpenAssistantChannel"/> to run a chat channel — a
/// Discord or Slack bot — as a second door onto the assistant's own standing conversation (AC-1023). The host
/// knows nothing of either platform: it takes messages in, hands transcript rows out, and relays consent.
/// </summary>
/// <param name="Id">A stable key of the plugin's own for this channel instance. Opening again under the same id replaces the channel already open under it rather than adding a second.</param>
/// <param name="Name">What the operator sees this channel called, e.g. <c>"Discord: #cockpit"</c>.</param>
/// <param name="Access">Who may talk to the assistant here. Enforced by the host on every inbound message, not left to the plugin's goodwill.</param>
/// <param name="Verbosity">How much of the transcript to relay. Read by the plugin, which does the translating.</param>
public sealed record AssistantChannelContribution(
    string Id,
    string Name,
    AssistantChannelAccess Access,
    AssistantChannelVerbosity Verbosity = AssistantChannelVerbosity.FinalAnswerOnly);
