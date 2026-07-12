namespace Cockpit.Core.Claude;

/// <summary>
/// Base type for every typed event a <see cref="Abstractions.Claude.ISessionDriver"/> can raise.
/// Mirrors the real stream-json event vocabulary emitted by the <c>claude</c> CLI in
/// <c>--input-format stream-json --output-format stream-json --verbose --include-partial-messages</c>
/// mode, as captured against a live process — see
/// <c>Memory/Zyra-Voice/StreamJson-Schema.md</c> for the ground-truth field tables this model
/// and <see cref="Infrastructure.Claude.ClaudeStreamJsonParser"/> are built against.
/// </summary>
public abstract record ClaudeSessionEvent
{
    /// <summary>Session id reported by the CLI's <c>system/init</c> event, once known.</summary>
    public required string? SessionId { get; init; }

    /// <summary>
    /// Non-null when this event belongs to a nested Task/sub-agent tool call rather than the
    /// top-level conversation; carried verbatim from the wrapper so a future agent-tree view
    /// can attribute events to their owning sub-agent.
    /// </summary>
    public string? ParentToolUseId { get; init; }

    /// <summary>Wrapper-level event uuid, when the wire event carries one.</summary>
    public string? Uuid { get; init; }
}

/// <summary>
/// Session-level metadata reported once at start of stream (<c>{"type":"system","subtype":"init",...}</c>).
/// </summary>
public sealed record SessionInitialized : ClaudeSessionEvent
{
    public required string Cwd { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
}

/// <summary>
/// An extended-thinking block, streamed separately from visible assistant text so the UI can
/// render it collapsed/dimmed. Covers both the <c>content_block_start</c> (empty
/// thinking/signature) and accumulated <c>thinking_delta</c>/<c>signature_delta</c> content.
/// </summary>
public sealed record AssistantThinkingDelta : ClaudeSessionEvent
{
    public required int BlockIndex { get; init; }
    public required string Thinking { get; init; }
}

/// <summary>
/// An incremental chunk of assistant text produced while streaming
/// (<c>{"type":"stream_event","event":{...content_block_delta text_delta...}}</c>).
/// </summary>
public sealed record AssistantTextDelta : ClaudeSessionEvent
{
    public required int BlockIndex { get; init; }
    public required string Text { get; init; }
}

/// <summary>
/// A complete assistant text block, as reported on the non-partial
/// <c>{"type":"assistant","message":{"content":[{"type":"text",...}]}}</c> event.
/// </summary>
public sealed record AssistantTextCompleted : ClaudeSessionEvent
{
    public required string Text { get; init; }
}

/// <summary>
/// The assistant requested a tool call
/// (<c>{"type":"assistant","message":{"content":[{"type":"tool_use",...}]}}</c>).
/// </summary>
public sealed record ToolUseRequested : ClaudeSessionEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public required string InputJson { get; init; }
}

/// <summary>
/// The result of a previously requested tool call
/// (<c>{"type":"user","message":{"content":[{"type":"tool_result",...}]}}</c>).
/// </summary>
public sealed record ToolResult : ClaudeSessionEvent
{
    public required string ToolUseId { get; init; }
    public required string Content { get; init; }
    public required bool IsError { get; init; }
}

/// <summary>
/// Claude is asking the host to allow or deny a tool call. This is a host-side concept:
/// there is no single canonical wire event for it (it depends on the chosen permission
/// approach — <c>canUseTool</c> callback, <c>--permission-prompt-tool</c>, or a PreToolUse
/// hook). F-C1 surfaces every <see cref="ToolUseRequested"/> as a pending permission
/// decision that the UI can allow/deny read-only; see ClaudeCliSession for how the
/// decision is (not yet) fed back to the CLI process.
/// </summary>
public sealed record PermissionRequested : ClaudeSessionEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public required string InputJson { get; init; }
}

/// <summary>
/// Claude surfaced a clarifying question to the user as part of its assistant text.
/// F-C1 does not attempt to detect questions from prose; reserved for a future increment
/// (e.g. explicit question tool use). Not raised by <c>ClaudeCliSession</c> yet.
/// </summary>
public sealed record Question : ClaudeSessionEvent
{
    public required string Text { get; init; }
}

/// <summary>
/// A turn finished (<c>{"type":"result",...,"result":"...","session_id":...}</c>).
/// </summary>
public sealed record TurnCompleted : ClaudeSessionEvent
{
    public required string Subtype { get; init; }
    public required string? Result { get; init; }
    public required bool IsError { get; init; }
    public string? StopReason { get; init; }
    public string? TerminalReason { get; init; }

    /// <summary>Token usage reported in the result event's <c>usage</c> object (#8 token/cost meter), or null when the result carries none (e.g. an error subtype).</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Session cost in USD from the result's <c>total_cost_usd</c> (#8), or null when absent.</summary>
    public double? TotalCostUsd { get; init; }

    /// <summary>The CLI's own turn count for the session from <c>num_turns</c> (#8), or null when absent.</summary>
    public int? NumTurns { get; init; }
}

/// <summary>
/// Token counts from a <c>result</c> event's <c>usage</c> object (#8 token/cost meter). Carried on
/// <see cref="TurnCompleted.Usage"/>; how these accumulate across turns (running total vs per-turn delta) is a
/// consumer concern, not decided here — this record just mirrors what the CLI reported for one result.
/// </summary>
public sealed record TokenUsage(int InputTokens, int OutputTokens, int CacheReadInputTokens, int CacheCreationInputTokens)
{
    /// <summary>Input + output tokens including cache reads and creations — one number for a compact meter.</summary>
    public int Total => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

/// <summary>
/// Per-session/per-turn status and attention state, derived from the CLI's own
/// <c>system/post_turn_summary</c> and <c>system/notification</c> events — the stream already
/// carries the status/attention signal the cockpit needs, so this event is a direct mapping
/// rather than a host-side heuristic. See <c>StreamJson-Schema.md</c> "Relevantie voor de cockpit".
/// </summary>
public sealed record SessionStatusChanged : ClaudeSessionEvent
{
    /// <summary>From <c>post_turn_summary.status_category</c> (e.g. "review_ready"), or <see langword="null"/> when this update came from a notification only.</summary>
    public string? StatusCategory { get; init; }

    /// <summary>From <c>post_turn_summary.status_detail</c>.</summary>
    public string? StatusDetail { get; init; }

    /// <summary>From <c>post_turn_summary.needs_action</c>.</summary>
    public string? NeedsAction { get; init; }

    /// <summary>From <c>notification.text</c>, when this update came from a notification.</summary>
    public string? NotificationText { get; init; }

    /// <summary>From <c>notification.priority</c> (e.g. "immediate"), when this update came from a notification.</summary>
    public string? NotificationPriority { get; init; }
}

/// <summary>
/// Rate-limit status for the account driving this session
/// (<c>{"type":"rate_limit_event","rate_limit_info":{...}}</c>).
/// </summary>
public sealed record RateLimitInfo : ClaudeSessionEvent
{
    public required string Status { get; init; }
    public required string RateLimitType { get; init; }
    public long? ResetsAt { get; init; }
}

/// <summary>
/// Something went wrong in the session driver itself (process failure, parse failure, ...).
/// This is a driver-level event, not a wire event.
/// </summary>
public sealed record SessionError : ClaudeSessionEvent
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Forward-compat catch-all for a wire line whose <c>type</c>/<c>subtype</c>/block-type this
/// parser does not (yet) model. Carries the raw JSON so nothing is silently dropped and
/// nothing crashes on an unrecognized shape.
/// </summary>
public sealed record UnknownEvent : ClaudeSessionEvent
{
    public required string RawJson { get; init; }
}
