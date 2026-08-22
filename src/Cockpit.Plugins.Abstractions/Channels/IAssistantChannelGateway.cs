using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>
/// What kind of transcript row this is — the plugin's copy of the kinds the chat window renders.
/// </summary>
public enum AssistantChannelRowKind
{
    /// <summary>
    /// The assistant's own prose.
    /// </summary>
    AssistantText,

    /// <summary>
    /// A message from the operator, whichever door it came in by.
    /// </summary>
    UserText,

    /// <summary>
    /// A tool call the assistant made.
    /// </summary>
    ToolUse,

    /// <summary>
    /// What a tool call came back with.
    /// </summary>
    ToolResult,

    /// <summary>
    /// Streamed reasoning.
    /// </summary>
    Thinking,

    /// <summary>
    /// A question card put to the operator.
    /// </summary>
    Question,

    /// <summary>
    /// A turn finished, with whatever it reported.
    /// </summary>
    TurnCompleted,

    /// <summary>
    /// A failure the driver reported.
    /// </summary>
    Error,

    /// <summary>
    /// A rule across the transcript, e.g. "context cleared".
    /// </summary>
    Divider,
}

/// <summary>
/// One row of the assistant's transcript as a channel plugin sees it (AC-1023 §4) — the same collection the chat
/// window reads, flattened to something carrying no <c>Cockpit.App</c> types.
/// </summary>
public sealed record AssistantChannelRow
{
    /// <summary>
    /// Stable for the life of the row, so an update is recognisable as the same row rather than a new message.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Which kind of row this is, so the plugin can apply its <see cref="AssistantChannelVerbosity"/>.
    /// </summary>
    public required AssistantChannelRowKind Kind { get; init; }

    /// <summary>
    /// The row's text as it stands right now. A streaming row grows, so this is a snapshot rather than a delta.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// When the row arrived.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// The tool being called on a <see cref="AssistantChannelRowKind.ToolUse"/> row; null elsewhere.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// What the tool came back with, once it has; null while the call is still running.
    /// </summary>
    public string? ResultText { get; init; }

    /// <summary>
    /// False the first time a row is seen and true every time it is raised again because it changed — post on the
    /// first, edit on the rest.
    /// </summary>
    public bool IsUpdate { get; init; }
}

/// <summary>
/// A consent prompt waiting on the assistant, relayed to a channel so it can be answered from there (AC-1023 §5).
/// Render <see cref="ConsentRequest.Action"/> verbatim, never a summary of it.
/// </summary>
public sealed record AssistantChannelConsentPrompt(Guid Id, ConsentRequest Request, bool CanRemember);

/// <summary>
/// What came of handing a channel message to the assistant. <see cref="Ignored"/> is §3's case and not an error to
/// report back: the sender is not allowed, and the plugin stays silent.
/// </summary>
public sealed record AssistantChannelSendResult(bool Ok, bool Ignored, string? Error)
{
    /// <summary>
    /// The message reached the assistant by the same route as text typed in the chat window.
    /// </summary>
    public static AssistantChannelSendResult Sent() => new(true, false, null);

    /// <summary>
    /// The sender is not allowed on this channel. Say nothing back to them.
    /// </summary>
    public static AssistantChannelSendResult IgnoredSender() => new(false, true, null);

    /// <summary>
    /// Something else stopped it — the channel closed, the assistant refused — and <paramref name="error"/> says what.
    /// </summary>
    public static AssistantChannelSendResult Refused(string error) => new(false, false, error);
}

/// <summary>
/// The seam a channel plugin gets from <see cref="ICockpitHost.OpenAssistantChannel"/> (AC-1023): messages in,
/// transcript rows out, consent relayed both ways. Dispose to close the channel; nothing here ends the conversation.
/// </summary>
public interface IAssistantChannelGateway : IDisposable
{
    /// <summary>
    /// Hands a message from <paramref name="senderUserId"/> to the assistant, by the same route as typed text. The
    /// channel's own <see cref="AssistantChannelAccess"/> is checked here first.
    /// </summary>
    Task<AssistantChannelSendResult> SendAsync(string senderUserId, string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// A transcript row arrived or changed — never a replay of what was already there. Raised on the UI thread, one
    /// per change including streaming deltas, so a plugin posting to a rate-limited platform coalesces them itself.
    /// </summary>
    event EventHandler<AssistantChannelRow>? RowChanged;

    /// <summary>
    /// The assistant is waiting on a consent decision. Only its own prompts — another session's never reach a channel.
    /// </summary>
    event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    /// <summary>
    /// A relayed prompt was resolved, here or in the app, and its surface can come down.
    /// </summary>
    event EventHandler<Guid>? ConsentPromptClosed;

    /// <summary>
    /// Answers a prompt this channel was told about, exactly as the card in the app does. An id it was not told
    /// about is ignored, so learning one elsewhere is not a way in.
    /// </summary>
    void RespondToConsent(Guid promptId, ConsentOutcome outcome, bool remember = false);
}
