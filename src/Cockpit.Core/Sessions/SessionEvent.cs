namespace Cockpit.Core.Sessions;

// Base type for every typed event a `Abstractions.Sessions.ISessionDriver` can raise — the one vocabulary
// the whole app renders. Shaped on the richest source, the `claude` CLI's stream-json output; a provider
// that cannot produce an event simply never raises it (`SessionCapabilities` tells the UI up front).
public abstract record SessionEvent
{
    // Session id reported by the CLI's `system/init` event, once known.
    public required string? SessionId { get; init; }

    // Non-null when this event belongs to a nested Task/sub-agent tool call rather than the
    // top-level conversation; carried verbatim from the wrapper so a future agent-tree view
    // can attribute events to their owning sub-agent.
    public string? ParentToolUseId { get; init; }

    // Wrapper-level event uuid, when the wire event carries one.
    public string? Uuid { get; init; }
}

// Session-level metadata reported once at start of stream (`{"type":"system","subtype":"init",...}`).
public sealed record SessionInitialized : SessionEvent
{
    public required string Cwd { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }

    // AC-141: the model the session actually started under, when the init event names it — only ever used
    // to seed a live-control's starting value, never fires a switch back at the driver.
    public string? Model { get; init; }
}

// An extended-thinking block, streamed separately from visible assistant text so the UI can
// render it collapsed/dimmed. Covers both the `content_block_start` (empty
// thinking/signature) and accumulated `thinking_delta`/`signature_delta` content.
public sealed record AssistantThinkingDelta : SessionEvent
{
    public required int BlockIndex { get; init; }
    public required string Thinking { get; init; }
}

// An incremental chunk of assistant text produced while streaming
// (`{"type":"stream_event","event":{...content_block_delta text_delta...}}`).
public sealed record AssistantTextDelta : SessionEvent
{
    public required int BlockIndex { get; init; }
    public required string Text { get; init; }
}

// A complete assistant text block, as reported on the non-partial
// `{"type":"assistant","message":{"content":[{"type":"text",...}]}}` event.
public sealed record AssistantTextCompleted : SessionEvent
{
    public required string Text { get; init; }
}

// The assistant requested a tool call
// (`{"type":"assistant","message":{"content":[{"type":"tool_use",...}]}}`).
public sealed record ToolUseRequested : SessionEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public required string InputJson { get; init; }
}

// The result of a previously requested tool call
// (`{"type":"user","message":{"content":[{"type":"tool_result",...}]}}`).
public sealed record ToolResult : SessionEvent
{
    public required string ToolUseId { get; init; }
    public required string Content { get; init; }
    public required bool IsError { get; init; }
}

// Claude is asking the host to allow or deny a tool call. Host-side concept, no single canonical wire event
// (depends on the chosen permission approach). F-C1 surfaces every `ToolUseRequested` as a pending decision
// the UI can allow/deny read-only; see ClaudeCliSession for how it is (not yet) fed back to the CLI.
public sealed record PermissionRequested : SessionEvent
{
    public required string ToolUseId { get; init; }
    public required string ToolName { get; init; }
    public required string InputJson { get; init; }
}

// Claude surfaced a clarifying question to the user as part of its assistant text.
// F-C1 does not attempt to detect questions from prose; reserved for a future increment
// (e.g. explicit question tool use). Not raised by `ClaudeCliSession` yet.
public sealed record Question : SessionEvent
{
    public required string Text { get; init; }
}

// A turn finished (`{"type":"result",...,"result":"...","session_id":...}`).
public sealed record TurnCompleted : SessionEvent
{
    public required string Subtype { get; init; }
    public required string? Result { get; init; }
    public required bool IsError { get; init; }
    public string? StopReason { get; init; }
    public string? TerminalReason { get; init; }

    // Token usage reported in the result event's `usage` object (#8 token/cost meter), or null when the result carries none (e.g. an error subtype).
    public TokenUsage? Usage { get; init; }

    // Session cost in USD from the result's `total_cost_usd` (#8), or null when absent.
    public double? TotalCostUsd { get; init; }

    // The CLI's own turn count for the session from `num_turns` (#8), or null when absent.
    public int? NumTurns { get; init; }

    // AC-410: why the turn failed, in the provider's own words. Null except for the one failure mode that
    // carries no `Result` to explain itself: an `error_during_execution` from an unrecognised `--resume` id.
    public IReadOnlyList<string>? Errors { get; init; }
}

// Token counts from a `result` event's `usage` object (#8 token/cost meter). Carried on
// `TurnCompleted.Usage`; how these accumulate across turns (running total vs per-turn delta) is a
// consumer concern, not decided here — this record just mirrors what the CLI reported for one result.
public sealed record TokenUsage(int InputTokens, int OutputTokens, int CacheReadInputTokens, int CacheCreationInputTokens)
{
    // Input + output tokens including cache reads and creations — one number for a compact meter.
    public int Total => InputTokens + OutputTokens + CacheReadInputTokens + CacheCreationInputTokens;
}

// Per-session/per-turn status and attention state, from the CLI's own `system/post_turn_summary` and
// `system/notification` events — a direct mapping, not a host-side heuristic (see `StreamJson-Schema.md`).
public sealed record SessionStatusChanged : SessionEvent
{
    // From `post_turn_summary.status_category` (e.g. "review_ready"), or `null` when this update came from a notification only.
    public string? StatusCategory { get; init; }

    // From `post_turn_summary.status_detail`.
    public string? StatusDetail { get; init; }

    // From `post_turn_summary.needs_action`.
    public string? NeedsAction { get; init; }

    // From `notification.text`, when this update came from a notification.
    public string? NotificationText { get; init; }

    // From `notification.priority` (e.g. "immediate"), when this update came from a notification.
    public string? NotificationPriority { get; init; }
}

// Rate-limit status for the account driving this session
// (`{"type":"rate_limit_event","rate_limit_info":{...}}`).
public sealed record RateLimitInfo : SessionEvent
{
    public required string Status { get; init; }
    public required string RateLimitType { get; init; }
    public long? ResetsAt { get; init; }
}

// What kind of outstanding work a `BackgroundTask` is. The host weighs the two differently (AC-276):
// a sub-agent keeps the session off "done", a shell only holds back the "session finished" notification — a
// dev server or `tail -f` would otherwise pin a session on "working" for as long as it runs.
public enum BackgroundTaskKind
{
    // A kind this build does not know. Ordinal 0 so an unmapped value is the least authoritative one.
    Unknown,

    // A nested agent (the Task tool) — work the operator is waiting on.
    SubAgent,

    // A backgrounded shell command that outlived the turn that started it.
    Shell,
}

// One piece of work that outlived its turn — see `BackgroundTasksChanged`.
public sealed record BackgroundTask(string TaskId, BackgroundTaskKind Kind, string? Description);

// The set of work outliving its turn changed (AC-276). `Tasks` is the *complete* set as of
// this event and never a delta — a dropped event self-corrects on the next one, where a start/stop ledger would
// strand the session on "working" the first time an end went missing.
public sealed record BackgroundTasksChanged : SessionEvent
{
    // Everything still outstanding. Empty when the last of it finished.
    public required IReadOnlyList<BackgroundTask> Tasks { get; init; }
}

// How a `BackgroundTaskNotification` says its task ended. Unknown is ordinal 0, deliberately: a status this
// build does not recognise lands on the least authoritative option rather than silently reading as completed.
public enum BackgroundTaskStatus
{
    Unknown,
    Completed,
    Failed,
}

// The provider's own verdict on a task named by an earlier `BackgroundTasksChanged` (AC-1057) — the only place
// completed and failed are told apart; that ledger only ever says "still there or not".
public sealed record BackgroundTaskNotification : SessionEvent
{
    public required string TaskId { get; init; }
    public string? ToolUseId { get; init; }
    public required BackgroundTaskStatus Status { get; init; }
}

// Something went wrong in the session driver itself (process failure, parse failure, ...).
// This is a driver-level event, not a wire event.
public sealed record SessionError : SessionEvent
{
    public required string Message { get; init; }
    public Exception? Exception { get; init; }

    // AC-720: which kind of failure this is, so the transcript can render blocking/temporary/informational
    // rows instead of identical plain text. Defaults to Unknown — a driver that has not been taught to
    // classify its errors yet renders exactly like today (informational, never a guessed red/amber).
    public SessionErrorKind Kind { get; init; } = SessionErrorKind.Unknown;

    public DateTimeOffset? RetryAfter { get; init; }
}

// Forward-compat catch-all for a wire line whose `type`/`subtype`/block-type this
// parser does not (yet) model. Carries the raw JSON so nothing is silently dropped and
// nothing crashes on an unrecognized shape.
public sealed record UnknownEvent : SessionEvent
{
    public required string RawJson { get; init; }
}
