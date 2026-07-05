namespace Zyra.Voice.Core.Claude;

/// <summary>
/// Base type for every typed event a <see cref="Abstractions.Claude.IClaudeSession"/> can raise.
/// Mirrors (a strict subset of) the stream-json event vocabulary emitted by the
/// <c>claude</c> CLI in <c>--input-format stream-json --output-format stream-json</c> mode.
/// See https://code.claude.com/docs/en/headless.md and
/// https://code.claude.com/docs/en/agent-sdk/streaming-vs-single-mode.md.
/// </summary>
public abstract record ClaudeSessionEvent
{
    /// <summary>Session id reported by the CLI's <c>system/init</c> event, once known.</summary>
    public required string? SessionId { get; init; }
}

/// <summary>
/// Session-level metadata reported once at start of stream (<c>{"type":"system","subtype":"init",...}</c>).
/// </summary>
public sealed record SessionInitialized : ClaudeSessionEvent
{
    public required string Model { get; init; }
    public required IReadOnlyList<string> Tools { get; init; }
}

/// <summary>
/// An incremental chunk of assistant text produced while streaming
/// (<c>{"type":"stream_event","event":{...content_block_delta text_delta...}}</c>).
/// </summary>
public sealed record AssistantTextDelta : ClaudeSessionEvent
{
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
