namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// Where a channel can deliver a plugin's message (AC-1544): a direct message to an allowed account, or a channel
/// or room, always inside that channel's own access list.
/// </summary>
public sealed record OperatorMessageTarget(string Id, string DisplayName);

/// <summary>
/// A message a plugin sends the operator through a channel (AC-1544): text, and optionally a question in the options
/// model of <see cref="AssistantChannelQuestion"/>, whose answer comes back under <see cref="CorrelationId"/>.
/// </summary>
public sealed record OperatorMessage
{
    /// <summary>
    /// The message itself.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// A question to answer with buttons, or with <see cref="AssistantChannelQuestion.FallbackPrompt"/> where there are none.
    /// </summary>
    public AssistantChannelQuestion? Question { get; init; }

    /// <summary>
    /// The key the answer comes back under; the host fills it in when a question has none.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Files and images that go out with the message, each under its own name.
    /// </summary>
    public IReadOnlyList<AssistantChannelAttachment> Attachments { get; init; } = [];
}

/// <summary>
/// What came of handing a message to a channel: delivered, or the reason it was not.
/// </summary>
public sealed record OperatorMessageResult(bool Ok, string? Error)
{
    /// <summary>
    /// The channel took the message.
    /// </summary>
    public static OperatorMessageResult Sent() => new(true, null);

    /// <summary>
    /// It did not, for <paramref name="error"/>.
    /// </summary>
    public static OperatorMessageResult Failed(string error) => new(false, error);
}

/// <summary>
/// The operator's answer to an <see cref="OperatorMessage"/>'s question, handed to the plugin that sent it: a click
/// or a text reply, from an account the channel's access list allows.
/// </summary>
public sealed record OperatorMessageAnswer(string CorrelationId, string SenderUserId, IReadOnlyList<string> Chosen);

/// <summary>
/// One open channel as a sending plugin sees it, with the targets it can deliver to.
/// </summary>
public sealed record OperatorMessageChannelInfo(string Id, string DisplayName, IReadOnlyList<OperatorMessageTarget> Targets);

/// <summary>
/// The sending half of a chat channel (AC-1544), handed to the host in <see cref="AssistantChannelContribution.Messages"/>
/// so any plugin can reach the operator through it. Answers come back through the channel's gateway.
/// </summary>
public interface IOperatorMessageChannel
{
    /// <summary>
    /// Where this channel can deliver right now.
    /// </summary>
    IReadOnlyList<OperatorMessageTarget> Targets { get; }

    /// <summary>
    /// Delivers <paramref name="message"/> to the target named <paramref name="targetId"/>, with its question as
    /// buttons carrying <see cref="OperatorMessage.CorrelationId"/>. A click goes to <see cref="IAssistantChannelGateway.AnswerAsync"/>.
    /// </summary>
    Task<OperatorMessageResult> SendAsync(string targetId, OperatorMessage message, CancellationToken cancellationToken = default);
}
