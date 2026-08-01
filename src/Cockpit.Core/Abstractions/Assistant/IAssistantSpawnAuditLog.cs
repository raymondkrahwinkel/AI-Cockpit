namespace Cockpit.Core.Abstractions.Assistant;

/// <summary>
/// The append-only trail of every session an agent asked the host to start or stop (AC-545, criterion 5).
/// </summary>
/// <remarks>
/// Same contract shape as the consent and delegation trails (<c>IConsentAuditLog</c>, <c>IDelegationAuditLog</c>)
/// so it inherits the shared <c>JsonlAuditLog&lt;T&gt;</c> machinery rather than growing a fourth implementation of
/// "append a line and never throw".
/// <para>
/// <b>Why a trail at all when the chat window already shows the tool call.</b> The transcript shows what happened
/// in <em>this</em> conversation; the trail answers "what has this thing ever started", across restarts and across
/// a conversation the operator has since scrolled past or replaced. A refused spawn is recorded too — a gate that
/// only logs what it let through cannot show you what it stopped.
/// </para>
/// </remarks>
public interface IAssistantSpawnAuditLog
{
    /// <summary>Appends one entry. Never throws: losing the record is bad, failing the operator's approved action because of it is worse.</summary>
    Task RecordAsync(AssistantSpawnAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>The most recent entries, newest first.</summary>
    Task<IReadOnlyList<AssistantSpawnAuditEntry>> ReadRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}

/// <summary>
/// One line of the trail. Criterion 5 names four things it must carry — caller, target workspace, profile and
/// working directory — and each is here as its own field rather than folded into a sentence, so the trail stays
/// something you can grep and the window can lay out in columns.
/// </summary>
/// <param name="At">When it happened.</param>
/// <param name="Action">What was asked for: <see cref="AssistantSpawnAction.Start"/> or <see cref="AssistantSpawnAction.Stop"/>.</param>
/// <param name="Caller">Which authority asked — the assistant, or a coordinator (AC-436). See <see cref="SpawnTarget"/>.</param>
/// <param name="CallerPaneId">The verified pane of a host-derived caller; null for the assistant, which has none.</param>
/// <param name="WorkspaceId">The desk it landed on (or would have).</param>
/// <param name="WorkspaceName">That desk's label at the time, kept because a workspace can be renamed or closed and the id then names nothing a reader recognises.</param>
/// <param name="Profile">The profile the session runs under — the field that says what it costs.</param>
/// <param name="WorkingDirectory">The folder it was started in, or null when the profile's default was used.</param>
/// <param name="PaneId">The pane that resulted, or null for a refusal.</param>
/// <param name="SessionName">What the pane is called, so the trail is readable without cross-referencing pane ids.</param>
/// <param name="Refusal">Why it did not happen, or null when it did. A trail without its refusals hides the gate working.</param>
public sealed record AssistantSpawnAuditEntry(
    DateTimeOffset At,
    AssistantSpawnAction Action,
    SpawnCaller Caller,
    string? CallerPaneId,
    string WorkspaceId,
    string? WorkspaceName,
    string? Profile,
    string? WorkingDirectory,
    string? PaneId,
    string? SessionName,
    string? Refusal);

/// <summary>What a trail entry records having been asked for.</summary>
public enum AssistantSpawnAction
{
    Start,
    Stop,
}
