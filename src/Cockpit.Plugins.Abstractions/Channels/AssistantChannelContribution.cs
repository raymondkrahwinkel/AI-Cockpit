namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// How much of the assistant's transcript a channel is worth relaying (AC-1023 §1.4). One setting per plugin
/// instance rather than per user; the translating into a platform message stays the plugin's.
/// </summary>
public enum AssistantChannelVerbosity
{
    /// <summary>
    /// A — the assistant's finished answers, nothing else.
    /// </summary>
    FinalAnswerOnly,

    /// <summary>
    /// B — everything the chat window shows, tool use included.
    /// </summary>
    Everything,

    /// <summary>
    /// C — short status lines in place of the full tool traffic.
    /// </summary>
    StatusLines,
}

/// <summary>
/// What a plugin hands the host via <see cref="ICockpitHost.OpenAssistantChannel"/> to run a chat channel — a
/// Discord or Slack bot — as a second door onto the assistant's own conversation (AC-1023).
/// </summary>
public sealed record AssistantChannelContribution
{
    /// <summary>
    /// A stable key of the plugin's own for this channel instance. Opening again under it replaces the channel
    /// already open rather than adding a second.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// What the operator sees this channel called, e.g. <c>"Discord: #cockpit"</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Who may talk to the assistant here, enforced by the host on every inbound message rather than left to the
    /// plugin's goodwill.
    /// </summary>
    public required AssistantChannelAccess Access { get; init; }

    /// <summary>
    /// How much of the transcript to relay. Read by the plugin, which does the translating.
    /// </summary>
    public AssistantChannelVerbosity Verbosity { get; init; } = AssistantChannelVerbosity.FinalAnswerOnly;
}
