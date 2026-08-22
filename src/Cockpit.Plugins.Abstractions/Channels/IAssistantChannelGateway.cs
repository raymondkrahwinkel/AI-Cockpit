using Cockpit.Plugins.Abstractions.Consent;

namespace Cockpit.Plugins.Abstractions.Channels;

/// <summary>What kind of transcript row this is — the plugin's copy of the kinds the chat window renders.</summary>
public enum AssistantChannelRowKind
{
    /// <summary>The assistant's own prose.</summary>
    AssistantText,

    /// <summary>A message from the operator, whichever door it came in by.</summary>
    UserText,

    /// <summary>A tool call the assistant made.</summary>
    ToolUse,

    /// <summary>What a tool call came back with.</summary>
    ToolResult,

    /// <summary>Streamed reasoning.</summary>
    Thinking,

    /// <summary>A question card put to the operator.</summary>
    Question,

    /// <summary>A turn finished, with whatever it reported.</summary>
    TurnCompleted,

    /// <summary>A failure the driver reported.</summary>
    Error,

    /// <summary>A rule across the transcript, e.g. "context cleared".</summary>
    Divider,
}

/// <summary>
/// One row of the assistant's transcript, as a channel plugin sees it (AC-1023 §4) — the same collection the chat
/// window reads, flattened to something with no <c>Cockpit.App</c> types in it.
/// </summary>
/// <param name="Id">Stable for the life of the row, so an update can be recognised as the same row rather than a new message.</param>
/// <param name="Kind">Which kind of row this is, so the plugin can apply its <see cref="AssistantChannelVerbosity"/>.</param>
/// <param name="Text">The row's text as it stands right now — a streaming row grows, so this is a snapshot, not a delta.</param>
/// <param name="Timestamp">When the row arrived.</param>
public sealed record AssistantChannelRow(Guid Id, AssistantChannelRowKind Kind, string Text, DateTimeOffset Timestamp)
{
    /// <summary>The tool being called, on a <see cref="AssistantChannelRowKind.ToolUse"/> row; null elsewhere.</summary>
    public string? ToolName { get; init; }

    /// <summary>What the tool came back with, once it has; null while the call is still running.</summary>
    public string? ResultText { get; init; }

    /// <summary>False the first time a row is seen, true every time it is raised again because it changed. A platform message is posted on the first and edited on the rest.</summary>
    public bool IsUpdate { get; init; }
}

/// <summary>
/// A consent prompt waiting on the assistant, relayed to a channel so it can be answered from there (AC-1023 §5) —
/// the same prompt the chat window shows its Allow/Deny card for.
/// </summary>
/// <param name="Id">Identifies the prompt when answering with <see cref="IAssistantChannelGateway.RespondToConsent"/>.</param>
/// <param name="Request">What is being asked. Render <see cref="ConsentRequest.Action"/> verbatim — never a summary of it.</param>
/// <param name="CanRemember">Whether "remember for this session" may be offered. Never true for a dangerous action.</param>
public sealed record AssistantChannelConsentPrompt(Guid Id, ConsentRequest Request, bool CanRemember);

/// <summary>
/// What came of handing a channel message to the assistant. <see cref="Ignored"/> is §3's case and is not an error
/// to report back: the sender is not allowed here, and the plugin stays silent rather than confirming a bot is listening.
/// </summary>
public sealed record AssistantChannelSendResult(bool Ok, bool Ignored, string? Error)
{
    /// <summary>The message went to the assistant, by the same route as text typed in the chat window.</summary>
    public static AssistantChannelSendResult Sent() => new(true, false, null);

    /// <summary>The sender is not allowed on this channel. Say nothing back to them.</summary>
    public static AssistantChannelSendResult IgnoredSender() => new(false, true, null);

    /// <summary>Something else stopped it — no assistant running, the channel closed — and <paramref name="error"/> says what.</summary>
    public static AssistantChannelSendResult Refused(string error) => new(false, false, error);
}

/// <summary>
/// The seam a channel plugin gets from <see cref="ICockpitHost.OpenAssistantChannel"/> (AC-1023): messages in,
/// transcript rows out, and the assistant's consent prompts relayed both ways. Everything the assistant itself is
/// stays on the host's side of it. Dispose to close the channel; nothing here ever ends the conversation.
/// </summary>
/// <remarks>
/// Events are raised on the UI thread and one per change, streaming deltas included — a plugin that posts to a
/// rate-limited platform coalesces them itself, since only it knows that platform's limits.
/// </remarks>
public interface IAssistantChannelGateway : IDisposable
{
    /// <summary>
    /// Hands a message from <paramref name="senderUserId"/> to the assistant, by the same route as text typed in
    /// the chat window. The channel's <see cref="AssistantChannelAccess"/> is checked here first — a sender it does
    /// not allow comes back as <see cref="AssistantChannelSendResult.IgnoredSender"/> and nothing is sent.
    /// </summary>
    Task<AssistantChannelSendResult> SendAsync(string senderUserId, string text, CancellationToken cancellationToken = default);

    /// <summary>A transcript row arrived or changed. Raised for what happens from now on, never a replay of what was already there.</summary>
    event EventHandler<AssistantChannelRow>? RowChanged;

    /// <summary>The assistant is waiting on a consent decision. Only its own prompts — another session's never reach a channel.</summary>
    event EventHandler<AssistantChannelConsentPrompt>? ConsentPromptOpened;

    /// <summary>A relayed prompt was resolved, here or in the app, and its surface can come down. Carries the prompt id.</summary>
    event EventHandler<Guid>? ConsentPromptClosed;

    /// <summary>
    /// Answers a prompt this channel was told about, exactly as the card in the app does. An id it was not told
    /// about is ignored, so learning one elsewhere is not a way in. <paramref name="remember"/> is honoured only
    /// for a prompt whose <see cref="AssistantChannelConsentPrompt.CanRemember"/> is true.
    /// </summary>
    void RespondToConsent(Guid promptId, ConsentOutcome outcome, bool remember = false);
}
